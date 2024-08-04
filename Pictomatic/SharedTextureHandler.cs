using Pictomatic.Common;
using ImGuiNET;
using ImGuiScene;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Drawing.Imaging;
using System.Drawing;
using System.Numerics;
using D3D = SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;
using SharpDX;
using SharpDX.Direct3D;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace Pictomatic;

internal class SharedTextureHandler : IDisposable
{
	private readonly TextureWrap _textureWrap;

	public SharedTextureHandler(IntPtr handle, Plugin plugin, InlayConfiguration overlayConfig)
	{
		D3D11.Texture2D? textureSource = DxHandler.Device?.OpenSharedResource<D3D11.Texture2D>(handle);
		if (textureSource is null)
		{
			throw new Exception("Could not initialize shared texture");
		}

		D3D11.ShaderResourceView view = new(DxHandler.Device, textureSource, new D3D11.ShaderResourceViewDescription { Format = textureSource.Description.Format, Dimension = D3D.ShaderResourceViewDimension.Texture2D, Texture2D = { MipLevels = textureSource.Description.MipLevels } });
		_textureWrap = new D3DTextureWrap(view, textureSource.Description.Width, textureSource.Description.Height);

		plugin.UpdateSharedDXTexture(textureSource, overlayConfig);
	}

	public void Dispose()
	{
		_textureWrap.Dispose();
	}

	public void Render()
	{
		//Disable Rendering on the original overlay
		//ImGui.Image(_textureWrap.ImGuiHandle, new Vector2(_textureWrap.Width, _textureWrap.Height));
	}
}