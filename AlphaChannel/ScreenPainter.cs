using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
//buffer plus the engine's real scene depth buffer directly from FFXIVClientStructs and draws straight
//into them - no ID3D11DeviceContext hook of any kind. (Hooking OMSetRenderTargets to find the scene depth
//buffer crashed the game reproducibly - turned out unnecessary anyway: RenderTargetManager already exposes
//it as a plain field. The swapchain's own DepthStencil, by contrast, only ever holds a stale clear value
//by Present time - confirmed via readback - which is why depth testing against it never worked.)
internal sealed unsafe class ScreenPainter : IDisposable
{
	//Base quad size before Scale is applied - rough starting guess, tune live in-game.
	private const float BaseWidth = 1.0f;
	private const float BaseHeight = 0.6f;

	//Absolute world transform for the screen quad. Unlike the old companion-relative offset, this is a
	//fixed world position/yaw set once per "spawn" (see Core.SpawnScreenInFrontOfLocalPlayer) and updated
	//live from either the local Settings UI or a remote player's synced state - it does not track any
	//game object's movement afterwards.
	internal Vector3 WorldPosition;
	internal float WorldYaw;
	internal float Scale = 1.0f;

	private readonly VertexShader _vs;
	private readonly PixelShader _ps;
	private readonly SamplerState _sampler;
	private readonly RasterizerState _rasterState;
	private readonly DepthStencilState _depthState;
	private readonly Buffer _cbuf;

	private Texture2D? _texture;
	private ShaderResourceView? _srv;

	//Wrapping the swapchain's own persistent back buffer/depth buffer views via SharpDX AddRefs/Releases them.
	//Doing that fresh every single frame (60+ times/sec) tears down the engine's own refcount on objects it
	//still needs - only re-wrap when the underlying pointer actually changes (e.g. on resize), and otherwise
	//reuse the cached wrapper for the draw.
	private nint _cachedRtvPtr;
	private nint _cachedDsvPtr;
	private RenderTargetView? _cachedRtv;
	private DepthStencilView? _cachedDsv;

	//TEMPORARY: backup diagnostic in case the reversed-Z/GreaterEqual guess is wrong - reads the real depth
	//at a near point (screen centre, where the player usually stands) and a far point (near the top of the
	//screen, usually background) so a wrong comparison direction shows up immediately in the log instead of
	//needing another round of "add more logging".
	private Texture2D? _depthProbeStagingTex;
	private DateTime _lastDepthProbeLog = DateTime.MinValue;

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

		//Reversed-Z (near=1, far=0) - the common convention for modern D3D11 engines, matched with a
		//reversed-Z projection matrix below so our own computed depth is on the same scale as the real buffer.
		_depthState = new DepthStencilState(DxHandler.Device, new DepthStencilStateDescription
		{
			IsDepthEnabled = true,
			DepthWriteMask = DepthWriteMask.Zero,
			DepthComparison = Comparison.GreaterEqual
		});

		_cbuf = new Buffer(DxHandler.Device, Marshal.SizeOf<ScreenParams>(), ResourceUsage.Default,
			BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

		DxHandler.OnPresent += DrawIfReady;
	}

	//Called from Core whenever the active video texture changes. Pass null to stop painting.
	internal void SetTarget(Texture2D? texture)
	{
		if (ReferenceEquals(texture, _texture))
		{
			return; //Nothing changed.
		}

		_srv?.Dispose();
		_srv = null;
		_texture = texture;

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

	//Cheap per-tick update of where/how big to draw the quad - called continuously so live Settings edits
	//and remote-synced host movement are reflected immediately, without touching the SRV.
	internal void SetTransform(Vector3 worldPosition, float worldYaw, float scale)
	{
		WorldPosition = worldPosition;
		WorldYaw = worldYaw;
		Scale = scale;
	}

	//Runs every frame from DxHandler's Present hook. Reads the swapchain's own current back buffer and
	//depth buffer straight out of FFXIVClientStructs (no context hook needed) and draws into them.
	private void DrawIfReady()
	{
		if (!TryGetSceneTargets(out nint rtvPtr, out nint dsvPtr, out uint targetWidth, out uint targetHeight) || _srv == null)
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

		ProbeDepthIfDue(ctx, dsv, targetWidth, targetHeight);

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

	private void ProbeDepthIfDue(DeviceContext ctx, DepthStencilView dsv, uint targetWidth, uint targetHeight)
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastDepthProbeLog).TotalSeconds < 2)
		{
			return;
		}
		_lastDepthProbeLog = now;

		try
		{
			using SharpDX.Direct3D11.Resource depthResource = dsv.Resource;
			using Texture2D depthTex = depthResource.QueryInterface<Texture2D>();
			SharpDX.DXGI.Format format = depthTex.Description.Format;

			if (_depthProbeStagingTex == null)
			{
				_depthProbeStagingTex = new Texture2D(DxHandler.Device, new Texture2DDescription
				{
					Width = 1,
					Height = 1,
					MipLevels = 1,
					ArraySize = 1,
					Format = format,
					SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
					Usage = ResourceUsage.Staging,
					BindFlags = BindFlags.None,
					CpuAccessFlags = CpuAccessFlags.Read,
					OptionFlags = ResourceOptionFlags.None
				});
			}

			string near = ReadDepthTexel(ctx, depthTex, format, (int)(targetWidth / 2), (int)(targetHeight / 2));
			string far = ReadDepthTexel(ctx, depthTex, format, (int)(targetWidth / 2), (int)(targetHeight * 0.1));
			Services.Log.Debug($"[ScreenPainter] depthProbe: fmt={format} near(centre)={near} far(top)={far}");
		}
		catch (Exception e)
		{
			Services.Log.Debug($"[ScreenPainter] depthProbe failed: {e.Message}");
		}
	}

	private string ReadDepthTexel(DeviceContext ctx, Texture2D depthTex, SharpDX.DXGI.Format format, int px, int py)
	{
		var region = new ResourceRegion(px, py, 0, px + 1, py + 1, 1);
		ctx.CopySubresourceRegion(depthTex, 0, region, _depthProbeStagingTex, 0);

		SharpDX.DataBox box = ctx.MapSubresource(_depthProbeStagingTex, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
		uint raw;
		unsafe
		{
			raw = *(uint*)box.DataPointer;
		}
		ctx.UnmapSubresource(_depthProbeStagingTex, 0);

		float decoded = format == SharpDX.DXGI.Format.R24G8_Typeless
			? (raw & 0x00FFFFFFu) / 16777215f
			: BitConverter.UInt32BitsToSingle(raw);

		return $"{decoded:0.000000}";
	}

	//Pure memory reads - no hooking, no calling into the game. Colour comes from the swapchain's own back
	//buffer (that's what's actually presented), but depth comes from RenderTargetManager's own DepthStencil
	//field - the real opaque-scene depth buffer, as opposed to the swapchain's own DepthStencil (which only
	//ever holds a stale clear value by this point in the frame).
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
		if (backBuffer == null || backBuffer->MipRenderTargets == null)
		{
			return false;
		}

		FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager* rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
		GfxKernel.Texture* sceneDepth = rtm != null ? rtm->DepthStencil : null;
		if (rtm == null || sceneDepth == null || sceneDepth->MipRenderTargets == null)
		{
			return false;
		}
		//Binding an RTV and DSV together in one draw call requires matching dimensions - if the internal
		//render resolution differs from the swapchain's (e.g. a "rendering resolution" scale setting below
		//100%), skip this frame rather than pairing mismatched targets.
		if (rtm->Resolution_Width != device->SwapChain->Width || rtm->Resolution_Height != device->SwapChain->Height)
		{
			return false;
		}

		rtvPtr = (nint)backBuffer->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView;
		dsvPtr = (nint)sceneDepth->MipRenderTargets[0].D3D11RenderTargetViewOrDepthStencilView;
		width = device->SwapChain->Width;
		height = device->SwapChain->Height;
		return rtvPtr != 0 && dsvPtr != 0;
	}

	private NumericsMatrix4x4? ComputeWorldViewProj()
	{
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
		NumericsMatrix4x4 proj = CreatePerspectiveFieldOfViewReversedZ(renderCamera->FoV, renderCamera->AspectRatio, renderCamera->NearPlane, renderCamera->FarPlane);

		NumericsMatrix4x4 world =
			NumericsMatrix4x4.CreateScale(BaseWidth * Scale, BaseHeight * Scale, 1f) *
			NumericsMatrix4x4.CreateFromAxisAngle(Vector3.UnitY, WorldYaw) *
			NumericsMatrix4x4.CreateTranslation(WorldPosition);

		return world * view * proj;
	}

	//Same X/Y as System.Numerics' right-handed CreatePerspectiveFieldOfView (M34=-1 layout), but with the Z
	//terms re-derived so near->1 and far->0 in NDC instead of near->0/far->1, matching FFXIV's reversed-Z
	//depth buffer.
	private static NumericsMatrix4x4 CreatePerspectiveFieldOfViewReversedZ(float fov, float aspect, float near, float far)
	{
		float yScale = 1f / MathF.Tan(fov / 2f);
		float xScale = yScale / aspect;

		return new NumericsMatrix4x4(
			xScale, 0, 0, 0,
			0, yScale, 0, 0,
			0, 0, near / (far - near), -1,
			0, 0, near * far / (far - near), 0);
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
		_depthProbeStagingTex?.Dispose();
		_cbuf.Dispose();
		_depthState.Dispose();
		_rasterState.Dispose();
		_sampler.Dispose();
		_ps.Dispose();
		_vs.Dispose();
	}
}
