using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;

// Die Elementvorlage "Leere Seite" wird unter https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x407 dokumentiert.

namespace Pictomatic.WebView2Window
{
    /// <summary>
    /// Eine leere Seite, die eigenständig verwendet oder zu der innerhalb eines Rahmens navigiert werden kann.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();

			// Create the WebView2 control
			var webView2 = new WebView2();
			// Set WebView2 as the content of the window
			Content = webView2;

			webView2.EnsureCoreWebView2Async().ContinueWith(task => //TODO CHANGE ENVIRONMENT TO THE TEXTURE: await webView2.EnsureCoreWebView2Async(environment);
			{
				if (task.Exception != null)
				{
					// Handle any error
					System.Diagnostics.Debug.WriteLine(task.Exception.Message);
					return;
				}

				// Once initialized, you can load a webpage
				webView2.CoreWebView2.Navigate("https://www.google.com");
			});




			webView2.Source = new Uri("https://www.google.com");
		}
    }
}
