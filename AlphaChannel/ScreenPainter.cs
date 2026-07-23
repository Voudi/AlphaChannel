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
//system entirely. Every frame (from DxHandler's Present hook) it reads the swapchain's own current
//back buffer and depth buffer directly from FFXIVClientStructs and draws straight into them - no
//ID3D11DeviceContext hook of any kind, since hooking OMSetRenderTargets crashed (most likely a conflict
//with an overlay also hooking that same vtable slot). Present is comparatively far less contested.
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
	private readonly DepthStencilState _noDepthState;
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

	private DateTime _lastDiagLog = DateTime.MinValue;
	private DateTime _lastTransformLog = DateTime.MinValue;
	private bool _lastHadTargets;
	private bool _lastWvpValid;
	private bool _drewSinceLastDiagLog;

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

			float4 PS(VOut i) : SV_TARGET
			{
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

		//DIAGNOSTIC: depth test disabled entirely for now. FFXIV's Render.Camera exposes a "StandardZ" flag
		//and two separate projection matrices, suggesting reversed-Z - the D3D11 default DepthFunc=Less would
		//silently reject our quad against that. Isolate visibility first, get the depth convention right after.
		_noDepthState = new DepthStencilState(DxHandler.Device, new DepthStencilStateDescription
		{
			IsDepthEnabled = false,
			DepthWriteMask = DepthWriteMask.Zero
		});

		_cbuf = new Buffer(DxHandler.Device, Marshal.SizeOf<ScreenParams>(), ResourceUsage.Default,
			BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

		DxHandler.OnPresent += DrawIfReady;

		Services.Log.Debug("[ScreenPainter] Initialized");
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

		Services.Log.Debug($"[ScreenPainter] SetTarget: texture={texture != null} companion={companion != null} @ {companion?.Position}");
	}

	private void LogDiagnosticsIfDue()
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastDiagLog).TotalSeconds < 2)
		{
			return;
		}
		_lastDiagLog = now;

		Services.Log.Debug($"[ScreenPainter] diag: srv={_srv != null} companion={_companion != null} hasTargets={_lastHadTargets} wvpValid={_lastWvpValid} drewSinceLast={_drewSinceLastDiagLog}");
		_drewSinceLastDiagLog = false;
	}

	//Runs every frame from DxHandler's Present hook. Reads the swapchain's own current back buffer and
	//depth buffer straight out of FFXIVClientStructs (no context hook needed) and draws into them - late
	//enough in the frame that the scene's opaque geometry is already there, still depth-tested against it.
	private void DrawIfReady()
	{
		bool hadTargets = TryGetSceneTargets(out nint rtvPtr, out nint dsvPtr);
		_lastHadTargets = hadTargets;
		LogDiagnosticsIfDue();

		if (!hadTargets || _srv == null || _companion == null)
		{
			return;
		}

		NumericsMatrix4x4? worldViewProj = ComputeWorldViewProj();
		_lastWvpValid = worldViewProj != null;
		if (worldViewProj == null)
		{
			return;
		}

		_drewSinceLastDiagLog = true;

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
			ctx.InputAssembler.InputLayout = null;
			ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
			ctx.Rasterizer.State = _rasterState;
			ctx.OutputMerger.BlendState = null;
			ctx.OutputMerger.DepthStencilState = _noDepthState;
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
	private static bool TryGetSceneTargets(out nint rtvPtr, out nint dsvPtr)
	{
		rtvPtr = 0;
		dsvPtr = 0;

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
		return rtvPtr != 0 && dsvPtr != 0;
	}

	private NumericsMatrix4x4? ComputeWorldViewProj()
	{
		if (_companion == null)
		{
			return null;
		}

		//The "active game camera" (Client.Game.Control.CameraManager) is a different object from the plain
		//scene-graph camera (Client.Graphics.Scene.CameraManager) we were reading before - go through the
		//game-level camera and down into its embedded scene camera instead of grabbing the scene one directly.
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
		if (camera->RenderCamera == null)
		{
			return null;
		}

		//No transpose here: our shader reads worldViewProj as row_major, matching System.Numerics' own
		//row-vector convention directly, so these need to go in as-is once M44 is corrected.
		NumericsMatrix4x4 view = ToNumerics(camera->ViewMatrix);
		view.M44 = 1.0f; //the raw value here isn't reliably 1, which throws off the homogeneous divide

		NumericsMatrix4x4 proj = ToNumerics(camera->RenderCamera->ProjectionMatrix);

		float yaw = _companion.Rotation;
		Vector3 pos = _companion.Position;
		Vector3 rotatedOffset = Vector3.Transform(LocalOffset, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));

		NumericsMatrix4x4 world =
			NumericsMatrix4x4.CreateScale(Width, Height, 1f) *
			NumericsMatrix4x4.CreateFromAxisAngle(Vector3.UnitY, yaw) *
			NumericsMatrix4x4.CreateTranslation(pos + rotatedOffset);

		LogTransformIfDue(pos, yaw, camera);

		return world * view * proj;
	}

	//FFXIVClientStructs' Matrix4x4/Vector3 have the same explicit field layout as their System.Numerics
	//equivalents, so a raw reinterpret is safe.
	private static NumericsMatrix4x4 ToNumerics(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 m)
		=> Unsafe.As<FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4, NumericsMatrix4x4>(ref m);

	private static Vector3 ToNumerics(FFXIVClientStructs.FFXIV.Common.Math.Vector3 v)
		=> Unsafe.As<FFXIVClientStructs.FFXIV.Common.Math.Vector3, Vector3>(ref v);

	private void LogTransformIfDue(Vector3 companionPos, float companionYaw, GfxScene.Camera* camera)
	{
		DateTime now = DateTime.UtcNow;
		if ((now - _lastTransformLog).TotalSeconds < 1)
		{
			return;
		}
		_lastTransformLog = now;

		Vector3 camPos = ToNumerics(camera->Position);
		Vector3 camLookAt = ToNumerics(camera->LookAtVector);
		Quaternion camRot = ToNumerics(camera->Rotation);

		Services.Log.Debug($"[ScreenPainter] xf: companionPos={companionPos} yaw={companionYaw:0.000} | camPos={camPos} camLookAt={camLookAt} camRot={camRot}");
	}

	private static Quaternion ToNumerics(FFXIVClientStructs.FFXIV.Common.Math.Quaternion q)
		=> Unsafe.As<FFXIVClientStructs.FFXIV.Common.Math.Quaternion, Quaternion>(ref q);

	public void Dispose()
	{
		DxHandler.OnPresent -= DrawIfReady;

		_srv?.Dispose();
		_cachedRtv?.Dispose();
		_cachedDsv?.Dispose();
		_cbuf.Dispose();
		_noDepthState.Dispose();
		_rasterState.Dispose();
		_sampler.Dispose();
		_ps.Dispose();
		_vs.Dispose();
	}
}
