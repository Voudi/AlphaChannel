using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;
using GfxKernel = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using GfxScene = FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using GfxGui = FFXIVClientStructs.FFXIV.Component.GUI;
using GameControl = FFXIVClientStructs.FFXIV.Client.Game.Control;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;

namespace AlphaChannel.Plugin.Video;

//X/Y are uv coordinates on the screen quad (0..1, matching the VS's own uv mapping - see the
//shader below for which corner is which), not world-space or pixel positions. Radius is also in
//that same 0..1 uv space, not pixels.
internal readonly record struct ReactionParticle(float X, float Y, float Alpha, float Radius, float R, float G, float B);

// Ported from AlphaChannel's ScreenPainter (Voudi, GPL-3.0, tag v1.1.20260725.1088) - the revamp that
// replaced the VFX/companion mounting (chara/monster/.../aetherstreamscreen_{session}.avfx cast on an
// actor) with a screen drawn directly in world space, independent of any game object. Paints our video
// texture directly into the 3D world at the TV's location as a screen-aligned quad, drawn with its own
// unlit shader so world lighting doesn't affect it - independent of the VFX/material system entirely.
// Every frame (from DxHandler's Present hook) it reads the swapchain's own current back buffer plus the
// engine's real scene depth buffer directly from FFXIVClientStructs and draws straight into them - no
// ID3D11DeviceContext hook of any kind. (Hooking OMSetRenderTargets to find the scene depth buffer crashed
// the game reproducibly - turned out unnecessary anyway: RenderTargetManager already exposes it as a plain
// field. The swapchain's own DepthStencil, by contrast, only ever holds a stale clear value by Present
// time - confirmed via readback - which is why depth testing against it never worked.)
internal sealed unsafe class ScreenPainter : IDisposable
{
	//Base quad size before Scale is applied - rough starting guess, tune live in-game.
	private const float BaseWidth = 1.0f;
	private const float BaseHeight = 0.6f;

	//Absolute world transform for the screen quad. Unlike the old companion-relative offset, this is a
	//fixed world position/yaw set once per "spawn" (see VideoEngine.SpawnScreenInFrontOfLocalPlayer) and
	//updated live from the Settings UI - it does not track any game object's movement afterwards.
	internal Vector3 WorldPosition;
	internal float WorldYaw;
	internal float Scale = 1.0f;

	private readonly VertexShader _vs;
    private readonly PixelShader _ps;
    private readonly PixelShader _loadingPs;
    private readonly PixelShader _glowPs;
    private readonly PixelShader _titlePs;
    private readonly PixelShader _reactionsPs;

    private volatile bool _isLoading;
    private DateTime _loadingStartedAtUtc = DateTime.MinValue;
    private readonly SamplerState _sampler;
	private readonly RasterizerState _rasterState;
	private readonly DepthStencilState _depthState;
	private readonly BlendState _alphaBlend;
	private readonly Buffer _cbuf;
	private readonly TitleTextureRenderer _titleRenderer = new();

	//Aetherphone addition on top of the upstream port - a soft ambient-light quad drawn larger than and
	//behind the real screen, so it reads as a light source in the room instead of a flat floating
	//rectangle. Fixed, not user-adjustable, same reasoning as Curvature below.
	private const float GlowScale = 1.28f;

	//The "now playing" banner shows for a while after the title actually changes, then fades rather
	//than sitting on screen for the whole video - DateTime-based rather than an accumulated
	//per-frame float so there is no drift and nothing to reset except on a genuine title change.
	private const float TitleHoldSeconds = 7f;
	private const float TitleFadeSeconds = 1f;
	private DateTime _titleShownAtUtc = DateTime.MinValue;

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

	//Native game UI (inventory, chat, hotbars, ...) is already fully composited into the back buffer by the
	//time our Present hook runs - only Dalamud's own ImGui overlay draws later than us. The scene depth
	//buffer has no concept of 2D UI at all, so depth testing alone can't stop us from painting over it; the
	//pixel shader discards fragments that fall inside any currently-visible native UI window's screen rect
	//instead, using bounds read straight from FFXIVClientStructs (AtkUnitBase/AtkResNode) - no hook needed.
	private const int MaxUiRects = 64;

	//AetherChannel addition - reactions rendered as simple GPU-drawn circles directly in
	//ReactionsPS below, not a texture (unlike the title banner). No font/emoji asset needed, and
	//per-frame updates are just a cbuffer write instead of a CPU rasterize + texture upload.
	private const int MaxReactions = 16;

	//Aetherphone addition on top of the upstream port - a slight cylindrical bow (curved-monitor look)
	//across the screen's width. There's no per-vertex buffer at all here (the flat 4-corner quad was
	//generated purely from SV_VertexID) - subdividing into a horizontal strip of quads and displacing
	//each column along local Z by a parabola (max at the edges, zero at center) is still just procedural
	//math in the vertex shader, no CPU-side geometry needed. Curvature is a fixed constant, not
	//user-adjustable - "slightly curved" was the ask, not a control to expose.
	private const int CurveSegments = 24;
	private const float Curvature = 0.12f;
	private const int VertexCount = (CurveSegments + 1) * 2;

	[StructLayout(LayoutKind.Sequential)]
	private unsafe struct ScreenParams
	{
		public NumericsMatrix4x4 WorldViewProj;
		public int UiRectCount;
		public float Curvature;
		public float GlowScale;
		public float TitleAlpha;
		public fixed float UiRects[MaxUiRects * 4]; //Each rect: screenX, screenY, width, height (pixels).

        //Kept as its own 16-byte-aligned group (float, not int, so there's no int/float3 packing
        //ambiguity to worry about across the C#/HLSL boundary) before the two reaction arrays.
        public float ReactionCount;
        public float LoadingTime;
        public float ReactionPad1;
        public float ReactionPad2;
        public fixed float ReactionPos[MaxReactions * 4]; //xy = uv center (0..1), z = alpha, w = radius.
		public fixed float ReactionColor[MaxReactions * 4]; //rgb = color, a unused.
	}

	internal ScreenPainter()
	{
		const string hlsl = @"
			#define MAX_UI_RECTS 64
			#define CURVE_SEGMENTS 24
			#define MAX_REACTIONS 16
				#define SCREEN_ASPECT (1.0 / 0.6)
			cbuffer Params : register(b0)
			{
				row_major float4x4 worldViewProj;
				int uiRectCount;
				float curvature;
				float glowScale;
				float titleAlpha;
				float4 uiRects[MAX_UI_RECTS]; //xy = screen pos, zw = size, in pixels
				float4 reactionMeta;
// x = reaction count
// y = loading animation time in seconds
				float4 reactionPos[MAX_REACTIONS]; //xy = uv center, z = alpha, w = radius
				float4 reactionColor[MAX_REACTIONS]; //rgb = color
			};
			struct VOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

			// Procedural horizontal strip: column = id/2 (0..CURVE_SEGMENTS), row = id%2 (0 = bottom,
			// 1 = top). Same flat quad as before when curvature is 0 - this just adds columns between
			// the two original edges instead of replacing them.
			VOut VS(uint id : SV_VertexID)
			{
				uint col = id / 2;
				uint row = id % 2;
				float x = -1.0 + 2.0 * (float)col / (float)CURVE_SEGMENTS;
				float y = row == 0 ? -1.0 : 1.0;
				float z = curvature * x * x; //Parabola: 0 at center, max at the edges.

				VOut o;
				o.pos = mul(float4(x, y, z, 1), worldViewProj);
				o.uv = float2((x + 1.0) * 0.5, row == 0 ? 1.0 : 0.0);
				return o;
			}

			Texture2D tex : register(t0);
			SamplerState smp : register(s0);

float4 LoadingPS(VOut i, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
{
    for (int r = 0; r < uiRectCount; r++)
    {
        float4 rect = uiRects[r];

        if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
            i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
        {
            discard;
        }
    }
    // Keep the physical back of the screen behaving exactly like the normal
    // video shader instead of mirroring the loading artwork.
    if (!isFrontFace)
    {
        return float4(0.015, 0.015, 0.02, 1.0); // subtle black plastic back
    }


    // Deep blue/purple background.
    float2 uv = i.uv;
    float3 bgTop    = float3(0.025, 0.020, 0.075);
    float3 bgBottom = float3(0.008, 0.010, 0.030);
    float3 color = lerp(bgTop, bgBottom, uv.y);


    // Very subtle purple illumination behind the spinner.
    float2 center = float2(0.5, 0.43);
    float2 p = uv - center;
    p.x *= SCREEN_ASPECT;


    float d = length(p);
    float halo = 1.0 - smoothstep(0.05, 0.36, d);
    color += float3(0.15, 0.035, 0.32) * halo * 0.32;


    // Animated circular spinner.
    float radius = 0.095;
    float thickness = 0.012;


    float ring =
        smoothstep(radius + thickness, radius, d) *
        smoothstep(radius - thickness, radius, d);


    float angle = atan2(p.y, p.x);
    float phase = angle - reactionMeta.y * 3.0;


    // Bright head with a fading tail around the ring.
    float wrapped = frac(phase / 6.2831853 + 1.0);
    float spinnerAlpha = pow(wrapped, 2.4);


    float3 dimPurple = float3(0.20, 0.07, 0.38);
    float3 hotPurple = float3(0.72, 0.28, 1.00);
    float3 spinner = lerp(dimPurple, hotPurple, spinnerAlpha);


    color = lerp(color, spinner, ring * (0.25 + spinnerAlpha * 0.75));


    // Small bright spinner head.
    float headAngle = reactionMeta.y * 3.0;
    float2 head =
        center +
        float2(cos(headAngle), sin(headAngle)) *
        float2(radius / SCREEN_ASPECT, radius);


    float2 hp = uv - head;
    hp.x *= SCREEN_ASPECT;


    float headGlow =
        1.0 - smoothstep(0.0, 0.022, length(hp));


    color += float3(0.55, 0.20, 1.0) * headGlow;


    return float4(saturate(color), 1.0);
}

			float4 PS(VOut i, bool isFrontFace : SV_IsFrontFace) : SV_TARGET
			{
				for (int r = 0; r < uiRectCount; r++)
				{
					float4 rect = uiRects[r];
					if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
						i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
					{
						discard;
					}
				}

				if (!isFrontFace)
				{
					return float4(0.015, 0.015, 0.02, 1.0); // subtle black plastic back
				}
				return tex.Sample(smp, i.uv);
			}

			// Same procedural geometry as VS above, drawn scaled up (via a separate, larger worldViewProj
			// computed on the C# side) - glowScale tells this shader how much bigger than the real screen
			// the current quad is, so it can remap its own 0..1 uv back down to the real screen's uv space
			// before sampling. A cheap 8-tap box blur plus a smoothstep fade toward this quad's own edges
			// turns that into a soft, edge-faded halo instead of just a bigger sharp copy of the video.
			float4 GlowPS(VOut i) : SV_TARGET
			{
				for (int r = 0; r < uiRectCount; r++)
				{
					float4 rect = uiRects[r];
					if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
						i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
					{
						discard;
					}
				}

				float2 centered = i.uv * 2.0 - 1.0;
				float edgeDist = max(abs(centered.x), abs(centered.y));
				float fade = 1.0 - smoothstep(0.55, 1.0, edgeDist);
				if (fade <= 0.0)
				{
					discard;
				}

				float2 sampleUv = centered * glowScale * 0.5 + 0.5;
				float2 texel = float2(0.01, 0.01) * glowScale;
				float4 sum = tex.Sample(smp, sampleUv);
				sum += tex.Sample(smp, sampleUv + float2(texel.x, 0));
				sum += tex.Sample(smp, sampleUv - float2(texel.x, 0));
				sum += tex.Sample(smp, sampleUv + float2(0, texel.y));
				sum += tex.Sample(smp, sampleUv - float2(0, texel.y));
				sum += tex.Sample(smp, sampleUv + texel);
				sum += tex.Sample(smp, sampleUv - texel);
				sum += tex.Sample(smp, sampleUv + float2(texel.x, -texel.y));
				sum /= 8.0;

				return float4(sum.rgb, sum.a * fade * 0.45);
			}

			// The ""now playing"" banner texture, composited on top of the sharp screen quad using the exact
			// same worldViewProj/geometry as the main draw - it is the same physical rectangle, just with a
			// different pixel shader and texture bound. Only the bottom band of local uv (v close to 1, see
			// the VS above: row 0/bottom maps to uv.y = 1) shows the banner texture; everywhere else this
			// pass discards outright so the sharp video drawn underneath is left untouched.
			#define TITLE_BAND_START 0.82
			Texture2D titleTex : register(t1);

			float4 TitlePS(VOut i) : SV_TARGET
			{
				for (int r = 0; r < uiRectCount; r++)
				{
					float4 rect = uiRects[r];
					if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
						i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
					{
						discard;
					}
				}

				if (i.uv.y < TITLE_BAND_START)
				{
					discard;
				}

				float2 titleUv = float2(i.uv.x, (i.uv.y - TITLE_BAND_START) / (1.0 - TITLE_BAND_START));
				float4 color = titleTex.Sample(smp, titleUv);
				float finalAlpha = color.a * titleAlpha;
				if (finalAlpha <= 0.01)
				{
					discard;
				}
				return float4(color.rgb, finalAlpha);
			}

			// Reactions - drawn as plain GPU circles (signed-distance-style smoothstep falloff), not a
			// texture, so there's no font/emoji asset dependency and no per-frame CPU rasterize/upload
			// cost. Composited on the same worldViewProj/geometry as everything else above.
			float4 ReactionsPS(VOut i) : SV_TARGET
			{
				for (int r = 0; r < uiRectCount; r++)
				{
					float4 rect = uiRects[r];
					if (i.pos.x >= rect.x && i.pos.x < rect.x + rect.z &&
						i.pos.y >= rect.y && i.pos.y < rect.y + rect.w)
					{
						discard;
					}
				}

				float4 accum = float4(0, 0, 0, 0);
				int count = (int)reactionMeta.x;
				for (int k = 0; k < count; k++)
				{
					float2 pos = reactionPos[k].xy;
					float alpha = reactionPos[k].z;
					float radius = reactionPos[k].w;
					float dist = length(float2((i.uv.x - pos.x) * SCREEN_ASPECT, i.uv.y - pos.y));
					float shape = 1.0 - smoothstep(radius * 0.7, radius, dist);
					if (shape <= 0.0)
					{
						continue;
					}

					float a = shape * alpha;
					accum.rgb = accum.rgb * (1.0 - a) + reactionColor[k].rgb * a;
					accum.a = accum.a + a * (1.0 - accum.a);
				}

				if (accum.a <= 0.01)
				{
					discard;
				}
				return accum;
			}";

        using (var vsb = ShaderBytecode.Compile(hlsl, "VS", "vs_4_0"))
        using (var psb = ShaderBytecode.Compile(hlsl, "PS", "ps_4_0"))
        using (var loadingPsb = ShaderBytecode.Compile(hlsl, "LoadingPS", "ps_4_0"))
        using (var glowPsb = ShaderBytecode.Compile(hlsl, "GlowPS", "ps_4_0"))
        using (var titlePsb = ShaderBytecode.Compile(hlsl, "TitlePS", "ps_4_0"))
        using (var reactionsPsb = ShaderBytecode.Compile(hlsl, "ReactionsPS", "ps_4_0"))
        {
            _vs = new VertexShader(DxHandler.Device, vsb);
            _ps = new PixelShader(DxHandler.Device, psb);
            _loadingPs = new PixelShader(DxHandler.Device, loadingPsb);
            _glowPs = new PixelShader(DxHandler.Device, glowPsb);
            _titlePs = new PixelShader(DxHandler.Device, titlePsb);
            _reactionsPs = new PixelShader(DxHandler.Device, reactionsPsb);
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

		//Standard straight-alpha blending for the glow and title-banner passes - the main quad stays fully
		//opaque (BlendState = null further down), only these two extra passes need to blend with whatever
		//is already in the back buffer.
		var alphaBlendDescription = new BlendStateDescription();
		alphaBlendDescription.RenderTarget[0] = new RenderTargetBlendDescription
		{
			IsBlendEnabled = true,
			SourceBlend = BlendOption.SourceAlpha,
			DestinationBlend = BlendOption.InverseSourceAlpha,
			BlendOperation = BlendOperation.Add,
			SourceAlphaBlend = BlendOption.One,
			DestinationAlphaBlend = BlendOption.Zero,
			AlphaBlendOperation = BlendOperation.Add,
			RenderTargetWriteMask = ColorWriteMaskFlags.All,
		};
		_alphaBlend = new BlendState(DxHandler.Device, alphaBlendDescription);

		_cbuf = new Buffer(DxHandler.Device, sizeof(ScreenParams), ResourceUsage.Default,
			BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

		DxHandler.OnPresent += DrawIfReady;
	}

    internal void SetLoading(bool loading)
    {
        _isLoading = loading;

        if (loading)
        {
            _loadingStartedAtUtc = DateTime.UtcNow;
        }
    }

    //Called from VideoEngine whenever the active video texture changes. Pass null to stop painting.
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
	//are reflected immediately, without touching the SRV.
	internal void SetTransform(Vector3 worldPosition, float worldYaw, float scale)
	{
		WorldPosition = worldPosition;
		WorldYaw = worldYaw;
		Scale = scale;
	}

	//Called whenever the queue advances, watch-along metadata resolves, or a viewer's ViewingEntry
	//changes - a no-op on the renderer side unless the title/source pair actually changed. Only a
	//genuine change (not a repeat call with the same text, e.g. WatchAlongSession's per-tick push)
	//restarts the show-then-fade timer.
	internal void SetTitle(string title, string source)
	{
		if (_titleRenderer.SetText(title, source))
		{
			_titleShownAtUtc = DateTime.UtcNow;
		}
	}

	private IReadOnlyList<ReactionParticle> _reactions = System.Array.Empty<ReactionParticle>();

	//Called every tick from Plugin.cs with the current set of active reaction particles - drawn as
	//circles by ReactionsPS. An empty list is the normal idle state, not an error; DrawIfReady just
	//sends a ReactionCount of 0 and the shader discards immediately.
	internal void SetReactions(IReadOnlyList<ReactionParticle> reactions)
	{
		_reactions = reactions;
	}

	private void CollectReactions(ref ScreenParams p)
	{
		int count = Math.Min(_reactions.Count, MaxReactions);
		p.ReactionCount = count;
		for (int i = 0; i < count; i++)
		{
			ReactionParticle r = _reactions[i];
			p.ReactionPos[i * 4 + 0] = r.X;
			p.ReactionPos[i * 4 + 1] = r.Y;
			p.ReactionPos[i * 4 + 2] = r.Alpha;
			p.ReactionPos[i * 4 + 3] = r.Radius;
			p.ReactionColor[i * 4 + 0] = r.R;
			p.ReactionColor[i * 4 + 1] = r.G;
			p.ReactionColor[i * 4 + 2] = r.B;
			p.ReactionColor[i * 4 + 3] = 0f;
		}
	}

	//1 for the first TitleHoldSeconds after a genuine title change, then ramps down to 0 over the
	//following TitleFadeSeconds instead of just vanishing.
	private float ComputeTitleAlpha()
	{
		double elapsed = (DateTime.UtcNow - _titleShownAtUtc).TotalSeconds;
		if (elapsed < TitleHoldSeconds)
		{
			return 1f;
		}

		double fadeProgress = (elapsed - TitleHoldSeconds) / TitleFadeSeconds;
		return (float)Math.Clamp(1.0 - fadeProgress, 0.0, 1.0);
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

		//Same camera/position math, just a larger quad - see GlowScale's own comment above. Re-reads the
		//camera a second time rather than reusing worldViewProj's own result, so in the extremely unlikely
		//case the camera vanishes between the two calls, this frame just skips the glow pass instead of
		//crashing inside the Present hook.
		NumericsMatrix4x4? glowWorldViewProj = ComputeWorldViewProj(GlowScale);
		ShaderResourceView? titleSrv = _titleRenderer.Srv;
		float titleAlpha = ComputeTitleAlpha();

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
            var p = new ScreenParams
            {
                WorldViewProj = worldViewProj.Value,
                Curvature = Curvature,
                TitleAlpha = titleAlpha,
                LoadingTime = _isLoading
                    ? (float)(DateTime.UtcNow - _loadingStartedAtUtc).TotalSeconds
                    : 0f
            };

            p.UiRectCount = CollectUiRects(ref p);
            CollectReactions(ref p);

            ctx.OutputMerger.SetRenderTargets(dsv, rtv);
			//Explicit full-target viewport - we never set this before, so whatever viewport the game's last
			//draw call before Present left bound (could be a sub-region: UI element, shadow pass, anything)
			//stayed active, meaning our correctly-computed NDC(0,0) landed at that viewport's center instead
			//of the actual screen center. That's the "math is right but it's drawn in the wrong place" gap.
			ctx.Rasterizer.SetViewport(0, 0, targetWidth, targetHeight, 0, 1);
			ctx.InputAssembler.InputLayout = null;
			ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
			ctx.Rasterizer.State = _rasterState;
			ctx.OutputMerger.DepthStencilState = _depthState;
			ctx.VertexShader.Set(_vs);
			ctx.VertexShader.SetConstantBuffer(0, _cbuf);
			ctx.PixelShader.SetConstantBuffer(0, _cbuf);
			ctx.PixelShader.SetShaderResource(0, _srv);
			ctx.PixelShader.SetSampler(0, _sampler);

        //Glow first, so the sharp screen quad drawn right after composites on top of it.
        if (!_isLoading && glowWorldViewProj != null)
        {
				ScreenParams glowParams = p;
				glowParams.WorldViewProj = glowWorldViewProj.Value;
				glowParams.GlowScale = GlowScale;
				ScreenParams* glowPp = &glowParams;
				ctx.UpdateSubresource(new SharpDX.DataBox((nint)glowPp), _cbuf);

				ctx.OutputMerger.BlendState = _alphaBlend;
				ctx.PixelShader.Set(_glowPs);
				ctx.Draw(VertexCount, 0);
			}

			//p is a stack local, its address is already stable, no `fixed` needed to take it.
			ScreenParams* pp = &p;
			ctx.UpdateSubresource(new SharpDX.DataBox((nint)pp), _cbuf);

        ctx.OutputMerger.BlendState = null;
        ctx.PixelShader.Set(_isLoading ? _loadingPs : _ps);
        ctx.Draw(VertexCount, 0);

        //Now-playing banner, composited on top of the sharp quad - same worldViewProj/cbuffer already
        //bound from the main draw above, just a different pixel shader and a second texture.
        if (!_isLoading && titleSrv != null && titleAlpha > 0f)
        {
				ctx.OutputMerger.BlendState = _alphaBlend;
				ctx.PixelShader.Set(_titlePs);
				ctx.PixelShader.SetShaderResource(1, titleSrv);
				ctx.Draw(VertexCount, 0);
				ctx.PixelShader.SetShaderResource(1, null);
			}

        //Reactions, drawn on top of everything - same worldViewProj/cbuffer already bound and
        //uploaded from the main draw above (CollectReactions filled p's reaction fields before
        //that single UpdateSubresource call), no texture involved so no second shader resource
        //slot to bind/unbind either.
        if (!_isLoading && p.ReactionCount > 0)
        {
				ctx.OutputMerger.BlendState = _alphaBlend;
				ctx.PixelShader.Set(_reactionsPs);
				ctx.Draw(VertexCount, 0);
			}

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

	//Pure memory reads - walks every currently-loaded native UI window (AtkUnitBase) and, for the visible
	//ones, records its on-screen rect from its root node. No hook, no game calls - same risk profile as
	//TryGetSceneTargets below.
	private static int CollectUiRects(ref ScreenParams p)
	{
		GfxGui.AtkStage* stage = GfxGui.AtkStage.Instance();
		if (stage == null || stage->RaptureAtkUnitManager == null)
		{
			return 0;
		}

		//Some loaded addons are invisible-but-"IsVisible" full-screen backdrops/overlays (loading-screen
		//fades, click-blockers, etc.), not actual windows - a single one of those among AllLoadedUnitsList
		//would otherwise mask out the entire draw. Real windows never span nearly the whole screen.
		float maxWidth = stage->ScreenSize.Width * 0.9f;
		float maxHeight = stage->ScreenSize.Height * 0.9f;

		int count = 0;
		System.Span<FFXIVClientStructs.Interop.Pointer<GfxGui.AtkUnitBase>> entries = stage->RaptureAtkUnitManager->AllLoadedUnitsList.Entries;
		for (int i = 0; i < entries.Length && count < MaxUiRects; i++)
		{
			GfxGui.AtkUnitBase* unit = entries[i].Value;
			if (unit == null || !unit->IsVisible || unit->Alpha == 0 || unit->RootNode == null || unit->RootNode->Color.A == 0 || unit->WindowNode == null)
			{
				continue;
			}

			//GetBounds is the same engine-computed bounding box FFXIV itself uses for mouse-collision testing
			//against this node - the actual current rendered extent, in real screen pixels, accounting for
			//scale/resolution/borders already. No guessing at Width/Height/ScreenX/Y or a fudge-factor padding.
			FFXIVClientStructs.FFXIV.Common.Math.Bounds bounds;
			unit->RootNode->GetBounds(&bounds);

			float x = bounds.Pos1.X;
			float y = bounds.Pos1.Y;
			float width = bounds.Pos2.X - bounds.Pos1.X;
			float height = bounds.Pos2.Y - bounds.Pos1.Y;
			if (width <= 0 || height <= 0 || width >= maxWidth || height >= maxHeight)
			{
				continue;
			}

			int baseIndex = count * 4;
			p.UiRects[baseIndex + 0] = x;
			p.UiRects[baseIndex + 1] = y;
			p.UiRects[baseIndex + 2] = width;
			p.UiRects[baseIndex + 3] = height;
			count++;
		}

		return count;
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

	//extraScale multiplies on top of the screen's own Scale, purely for the glow pass drawing a larger
	//copy of the same quad - 1 for the real screen (the default, used everywhere else).
	private NumericsMatrix4x4? ComputeWorldViewProj(float extraScale = 1f)
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

		float scale = Scale * extraScale;
		NumericsMatrix4x4 world =
			// Z scales with Scale too, not left at a flat 1 - otherwise the curvature depth (which is
			// expressed in the same pre-scale local units as X/Y) would look progressively flatter as
			// the screen is resized up, instead of bowing proportionally to its own size.
			NumericsMatrix4x4.CreateScale(BaseWidth * scale, BaseHeight * scale, scale) *
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

		_titleRenderer.Dispose();
		_srv?.Dispose();
		_cachedRtv?.Dispose();
		_cachedDsv?.Dispose();
		_cbuf.Dispose();
		_alphaBlend.Dispose();
		_depthState.Dispose();
		_rasterState.Dispose();
		_sampler.Dispose();
        _titlePs.Dispose();
        _reactionsPs.Dispose();
        _glowPs.Dispose();
        _loadingPs.Dispose();
        _ps.Dispose();
        _vs.Dispose();
    }
}
