using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.Types;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using GfxKernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using GfxScene = FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GameControl = FFXIVClientStructs.FFXIV.Client.Game.Control;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;

namespace AlphaChannel;

//Paints our video texture directly into the 3D world at the TV's location as a screen-aligned quad,
//drawn with its own unlit shader so world lighting doesn't affect it - independent of the VFX/material
//system entirely. Every frame (from DxHandler's Present hook) it reads the swapchain's own current back
//buffer and depth buffer directly from FFXIVClientStructs and draws straight into them - no
//ID3D11DeviceContext hook of any kind. Hooking OMSetRenderTargets (needed to find the real scene depth
//buffer for occlusion, since the swapchain's own DepthStencil only ever holds a stale clear value by
//Present time) crashes the game here, reproducibly, inside Dalamud's own hooking machinery - not a bug in
//our detour logic but an environment-level conflict. So: no depth testing, the quad always draws on top.
internal sealed unsafe class ScreenPainter : IDisposable
{
	//Rough starting guesses - tune live in-game until the quad lines up with the actual TV screen.
	internal Vector3 LocalOffset = new(0f, 1.0f, 0f);
	internal float Width = 1.0f;
	internal float Height = 0.6f;

	private readonly VertexShader _vs;
	private readonly PixelShader _ps;
	private readonly SamplerState _sampler;
	private readonly RasterizerState _rasterState;
	private readonly DepthStencilState _depthState;
	private readonly Buffer _cbuf;

	private Texture2D? _texture;
	private ShaderResourceView? _srv;
	private IGameObject? _companion;

	//Wrapping the swapchain's own persistent back buffer/depth buffer views via SharpDX AddRefs/Releases them.
	//Doing that fresh every single frame (60+ times/sec) tears down the engine's own refcount on objects it
	//still needs - only re-wrap when the underlying pointer actually changes (e.g. on resize), and otherwise
	//reuse the cached wrapper for the draw.
	private nint _cachedRtvPtr;
	private nint _cachedDsvPtr;
	private RenderTargetView? _cachedRtv;
	private DepthStencilView? _cachedDsv;

	[StructLayout(LayoutKind.Sequential)]
	private struct ScreenParams
	{
		public NumericsMatrix4x4 WorldViewProj;
	}

	internal ScreenPainter()
	{
		const string hlsl = @"
			cbuffer Params : register(b0) { row_major float4x4 worldViewProj; };
			struct VOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

			static const float2 corners[4] = { float2(-1,-1), float2(-1,1), float2(1,-1), float2(1,1) };
			static const float2 uvs[4]     = { float2(0,1),   float2(0,0), float2(1,1),  float2(1,0) };

			VOut VS(uint id : SV_VertexID)
			{
				VOut o;
				o.pos = mul(float4(corners[id], 0, 1), worldViewProj);
				o.uv = uvs[id];
				return o;
			}

			Texture2D tex : register(t0);
			SamplerState smp : register(s0);

			float4 PS(VOut i, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
			{
				if (!isFrontFace)
				{
					return float4(0.333, 0.333, 0.333, 1); //#555555 - back of the screen, not the (mirrored) video
				}
				return tex.Sample(smp, i.uv);
			}";

		using (var vsb = ShaderBytecode.Compile(hlsl, "VS", "vs_4_0"))
		using (var psb = ShaderBytecode.Compile(hlsl, "PS", "ps_4_0"))
		{
			_vs = new VertexShader(DxHandler.Device, vsb);
			_ps = new PixelShader(DxHandler.Device, psb);
		}

		_sampler = new SamplerState(DxHandler.Device, new SamplerStateDescription
		{
			Filter = Filter.MinMagMipLinear,
			AddressU = TextureAddressMode.Clamp,
			AddressV = TextureAddressMode.Clamp,
			AddressW = TextureAddressMode.Clamp,
			ComparisonFunction = Comparison.Never,
			MinimumLod = 0,
			MaximumLod = float.MaxValue
		});

		_rasterState = new RasterizerState(DxHandler.Device, new RasterizerStateDescription
		{
			FillMode = FillMode.Solid,
			CullMode = SharpDX.Direct3D11.CullMode.None
		});

		_depthState = new DepthStencilState(DxHandler.Device, new DepthStencilStateDescription
		{
			IsDepthEnabled = false,
			DepthWriteMask = DepthWriteMask.Zero
		});

		_cbuf = new Buffer(DxHandler.Device, Marshal.SizeOf<ScreenParams>(), ResourceUsage.Default,
			BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

		DxHandler.OnPresent += DrawIfReady;
	}

	//Called from Core whenever the active video texture / target TV changes. Pass null to stop painting.
	internal void SetTarget(Texture2D? texture, IGameObject? companion)
	{
		//Compare by address, not reference - the object table may hand out a fresh wrapper instance for the
		//same underlying actor on every scan, so reference equality alone would never skip the SRV rebuild.
		if (ReferenceEquals(texture, _texture) && companion?.Address == _companion?.Address)
		{
			return; //Nothing changed - avoid recreating the SRV on every 1s companion scan.
		}

		_srv?.Dispose();
		_srv = null;
		_texture = texture;
		_companion = companion;

		if (texture != null)
		{
			_srv = new ShaderResourceView(DxHandler.Device, texture, new ShaderResourceViewDescription
			{
				Format = texture.Description.Format,
				Dimension = ShaderResourceViewDimension.Texture2D,
				Texture2D = { MipLevels = texture.Description.MipLevels }
			});
		}
	}

	//Runs every frame from DxHandler's Present hook. Reads the swapchain's own current back buffer and
	//depth buffer straight out of FFXIVClientStructs (no context hook needed) and draws into them.
	private void DrawIfReady()
	{
		if (!TryGetSceneTargets(out nint rtvPtr, out nint dsvPtr, out uint targetWidth, out uint targetHeight) || _srv == null || _companion == null)
		{
			return;
		}

		NumericsMatrix4x4? worldViewProj = ComputeWorldViewProj();
		if (worldViewProj == null)
		{
			return;
		}

		//SharpDX's raw-pointer ComObject constructor does NOT AddRef, but its Dispose() unconditionally
		//Release()s - wrapping a borrowed pointer and disposing it later would silently over-release the
		//engine's own view. AddRef right after construction so our eventual Dispose() is actually balanced.
		if (rtvPtr != _cachedRtvPtr || _cachedRtv == null)
		{
			_cachedRtv?.Dispose();
			_cachedRtv = new RenderTargetView(rtvPtr);
			Marshal.AddRef(rtvPtr);
			_cachedRtvPtr = rtvPtr;
		}
		if (dsvPtr != _cachedDsvPtr || _cachedDsv == null)
		{
			_cachedDsv?.Dispose();
			_cachedDsv = new DepthStencilView(dsvPtr);
			Marshal.AddRef(dsvPtr);
			_cachedDsvPtr = dsvPtr;
		}

		RenderTargetView rtv = _cachedRtv;
		DepthStencilView dsv = _cachedDsv;

		DeviceContext ctx = DxHandler.Device!.ImmediateContext;

		RenderTargetView[] prevRtvs = ctx.OutputMerger.GetRenderTargets(1, out DepthStencilView? prevDsv);
		VertexShader? prevVs = ctx.VertexShader.Get();
		PixelShader? prevPs = ctx.PixelShader.Get();
		InputLayout? prevIl = ctx.InputAssembler.InputLayout;
		PrimitiveTopology prevTopo = ctx.InputAssembler.PrimitiveTopology;
		BlendState? prevBlend = ctx.OutputMerger.BlendState;
		DepthStencilState? prevDss = ctx.OutputMerger.DepthStencilState;
		RasterizerState? prevRs = ctx.Rasterizer.State;

		try
		{
			var p = new ScreenParams { WorldViewProj = worldViewProj.Value };
			ctx.UpdateSubresource(ref p, _cbuf);

			ctx.OutputMerger.SetRenderTargets(dsv, rtv);
			//Explicit full-target viewport - we never set this before, so whatever viewport the game's last
			//draw call before Present left bound (could be a sub-region: UI element, shadow pass, anything)
			//stayed active, meaning our correctly-computed NDC(0,0) landed at that viewport's center instead
			//of the actual screen center. That's the "math is right but it's drawn in the wrong place" gap.
			ctx.Rasterizer.SetViewport(0, 0, targetWidth, targetHeight, 0, 1);
			ctx.InputAssembler.InputLayout = null;
			ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
			ctx.Rasterizer.State = _rasterState;
			ctx.OutputMerger.BlendState = null;
			ctx.OutputMerger.DepthStencilState = _depthState;
			ctx.VertexShader.Set(_vs);
			ctx.VertexShader.SetConstantBuffer(0, _cbuf);
			ctx.PixelShader.Set(_ps);
			ctx.PixelShader.SetShaderResource(0, _srv);
			ctx.PixelShader.SetSampler(0, _sampler);
			ctx.Draw(4, 0);
			ctx.PixelShader.SetShaderResource(0, null);
		}
		finally
		{
			ctx.OutputMerger.SetRenderTargets(prevDsv, prevRtvs);
			foreach (RenderTargetView? prevRtv in prevRtvs)
			{
				prevRtv?.Dispose();
			}
			prevDsv?.Dispose();

			ctx.VertexShader.Set(prevVs); prevVs?.Dispose();
			ctx.PixelShader.Set(prevPs); prevPs?.Dispose();
			ctx.InputAssembler.InputLayout = prevIl; prevIl?.Dispose();
			ctx.InputAssembler.PrimitiveTopology = prevTopo;
			ctx.OutputMerger.BlendState = prevBlend; prevBlend?.Dispose();
			ctx.OutputMerger.DepthStencilState = prevDss; prevDss?.Dispose();
			ctx.Rasterizer.State = prevRs; prevRs?.Dispose();
		}
	}

	//Pure memory reads - no hooking, no calling into the game. The swapchain owns exactly one back buffer
	//and one depth buffer, each a Kernel.Texture whose mip-0 render target view doubles as its RTV or DSV.
	private static bool TryGetSceneTargets(out nint rtvPtr, out nint dsvPtr, out uint width, out uint height)
	{
		rtvPtr = 0;
		dsvPtr = 0;
		width = 0;
		height = 0;

		GfxKernel.Device* device = GfxKernel.Device.Instance();
		if (device == null || device->SwapChain == null)
		{
			return false;
		}

		GfxKernel.Texture* backBuffer = device->SwapChain->BackBuffer;
		GfxKernel.Texture* depthStencil = device->SwapChain->DepthStencil;
		if (backBuffer == null || depthStencil == null || backBuffer->MipRenderTargets == null || depthStencil->MipRenderTargets == null)
		{
			return false;
		}

		rtvPtr = (nint)backBuffer->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView;
		dsvPtr = (nint)depthStencil->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView;
		width = device->SwapChain->Width;
		height = device->SwapChain->Height;
		return rtvPtr != 0 && dsvPtr != 0;
	}

	private NumericsMatrix4x4? ComputeWorldViewProj()
	{
		if (_companion == null)
		{
			return null;
		}

		//The "active game camera" (Client.Game.Control.CameraManager) is a different object from the plain
		//scene-graph camera (Client.Graphics.Scene.CameraManager) - go through the game-level camera and
		//down into its embedded scene camera instead of grabbing the scene one directly.
		GameControl.CameraManager* gameCameraManager = GameControl.CameraManager.Instance();
		if (gameCameraManager == null)
		{
			return null;
		}

		FFXIVClientStructs.FFXIV.Client.Game.Camera* gameCamera = gameCameraManager->GetActiveCamera();
		if (gameCamera == null)
		{
			return null;
		}

		GfxScene.Camera* camera = &gameCamera->CameraBase.SceneCamera;
		FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera* renderCamera = camera->RenderCamera;
		if (renderCamera == null)
		{
			return null;
		}

		//Built from plain position/angle data rather than the game's own raw view/projection matrices -
		//confirmed correct for position/rotation.
		Vector3 camPos = ToNumerics(camera->Position);
		Vector3 camLookAt = ToNumerics(camera->LookAtVector);

		NumericsMatrix4x4 view = NumericsMatrix4x4.CreateLookAt(camPos, camLookAt, Vector3.UnitY);
		NumericsMatrix4x4 proj = NumericsMatrix4x4.CreatePerspectiveFieldOfView(renderCamera->FoV, renderCamera->AspectRatio, renderCamera->NearPlane, renderCamera->FarPlane);

		float yaw = _companion.Rotation;
		Vector3 pos = _companion.Position;
		Vector3 rotatedOffset = Vector3.Transform(LocalOffset, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));

		NumericsMatrix4x4 world =
			NumericsMatrix4x4.CreateScale(Width, Height, 1f) *
			NumericsMatrix4x4.CreateFromAxisAngle(Vector3.UnitY, yaw) *
			NumericsMatrix4x4.CreateTranslation(pos + rotatedOffset);

		return world * view * proj;
	}

	//FFXIVClientStructs' Vector3 has the same explicit field layout as System.Numerics', so a raw
	//reinterpret is safe.
	private static Vector3 ToNumerics(FFXIVClientStructs.FFXIV.Common.Math.Vector3 v)
		=> Unsafe.As<FFXIVClientStructs.FFXIV.Common.Math.Vector3, Vector3>(ref v);

	public void Dispose()
	{
		DxHandler.OnPresent -= DrawIfReady;

		_srv?.Dispose();
		_cachedRtv?.Dispose();
		_cachedDsv?.Dispose();
		_cbuf.Dispose();
		_depthState.Dispose();
		_rasterState.Dispose();
		_sampler.Dispose();
		_ps.Dispose();
		_vs.Dispose();
	}
}
