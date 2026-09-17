using Dalamud.Utility;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;

namespace AlphaChannel.Plugin.Video;

/// <summary>Uploads the compact browser frame and scales it into the shared TV texture on the render thread.</summary>
internal sealed class BrowserFrameScaler : IDisposable
{
    private readonly Texture2D source;
    private readonly ShaderResourceView sourceView;
    private readonly Texture2D renderTarget;
    private readonly RenderTargetView renderTargetView;
    private readonly Texture2D destination;
    private readonly VertexShader vertexShader;
    private readonly PixelShader pixelShader;
    private readonly SamplerState sampler;
    private readonly int sourceWidth;
    private readonly int sourceHeight;
    private bool disposed;

    internal BrowserFrameScaler(Texture2D destination, int width, int height)
    {
        this.destination = destination;
        sourceWidth = width;
        sourceHeight = height;
        var device = DxHandler.Device ?? throw new InvalidOperationException("The D3D device is unavailable.");
        source = new Texture2D(device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
        });
        sourceView = new ShaderResourceView(device, source);
        var targetDescription = destination.Description;
        renderTarget = new Texture2D(device, new Texture2DDescription
        {
            Width = targetDescription.Width, Height = targetDescription.Height, MipLevels = 1, ArraySize = 1,
            Format = targetDescription.Format, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        renderTargetView = new RenderTargetView(device, renderTarget);
        const string shader = """
            struct VOut { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            VOut VS(uint id : SV_VertexID) {
                VOut output;
                float2 p = float2((id << 1) & 2, id & 2);
                output.position = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
                output.uv = p;
                return output;
            }
            Texture2D image : register(t0);
            SamplerState imageSampler : register(s0);
            float4 PS(VOut input) : SV_TARGET { return image.Sample(imageSampler, input.uv); }
            """;
        using var vertexBytecode = ShaderBytecode.Compile(shader, "VS", "vs_4_0");
        using var pixelBytecode = ShaderBytecode.Compile(shader, "PS", "ps_4_0");
        vertexShader = new VertexShader(device, vertexBytecode);
        pixelShader = new PixelShader(device, pixelBytecode);
        sampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never, MinimumLod = 0, MaximumLod = float.MaxValue
        });
    }

    internal unsafe void Blit(byte[] frame)
    {
        if (disposed || frame.Length != sourceWidth * sourceHeight * 4) return;
        var context = DxHandler.Device?.ImmediateContext;
        if (context is null) return;
        fixed (byte* data = frame)
            context.UpdateSubresource(source, 0, null, (nint)data, sourceWidth * 4, 0);

        var previousTargets = context.OutputMerger.GetRenderTargets(1, out DepthStencilView? previousDepth);
        var previousRasterizer = context.Rasterizer.State;
        var previousBlend = context.OutputMerger.BlendState;
        var previousDepthState = context.OutputMerger.DepthStencilState;
        var previousVertexShader = context.VertexShader.Get();
        var previousPixelShader = context.PixelShader.Get();
        var previousLayout = context.InputAssembler.InputLayout;
        var previousTopology = context.InputAssembler.PrimitiveTopology;
        try
        {
            context.OutputMerger.SetRenderTargets(renderTargetView);
            context.Rasterizer.SetViewport(0, 0, destination.Description.Width, destination.Description.Height);
            context.Rasterizer.State = null;
            context.OutputMerger.BlendState = null;
            context.OutputMerger.DepthStencilState = null;
            context.InputAssembler.InputLayout = null;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.VertexShader.Set(vertexShader);
            context.PixelShader.Set(pixelShader);
            context.PixelShader.SetShaderResource(0, sourceView);
            context.PixelShader.SetSampler(0, sampler);
            context.Draw(3, 0);
            context.PixelShader.SetShaderResource(0, null);
            context.OutputMerger.ResetTargets();
            context.CopyResource(renderTarget, destination);
        }
        finally
        {
            context.OutputMerger.SetRenderTargets(previousDepth, previousTargets);
            foreach (var target in previousTargets) target?.Dispose();
            previousDepth?.Dispose();
            context.Rasterizer.State = previousRasterizer;
            context.OutputMerger.BlendState = previousBlend;
            context.OutputMerger.DepthStencilState = previousDepthState;
            context.InputAssembler.InputLayout = previousLayout;
            context.InputAssembler.PrimitiveTopology = previousTopology;
            context.VertexShader.Set(previousVertexShader);
            context.PixelShader.Set(previousPixelShader);
            previousRasterizer?.Dispose(); previousBlend?.Dispose(); previousDepthState?.Dispose();
            previousLayout?.Dispose(); previousVertexShader?.Dispose(); previousPixelShader?.Dispose();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        sampler.Dispose(); pixelShader.Dispose(); vertexShader.Dispose();
        renderTargetView.Dispose(); renderTarget.Dispose(); sourceView.Dispose(); source.Dispose();
    }
}
