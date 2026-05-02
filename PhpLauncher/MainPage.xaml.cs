using Microsoft.Extensions.Options;
using PhpLauncher.ServerLoader;

namespace PhpLauncher
{
    /// <summary>
    /// Main application page: hosts a WebView with error handling and load-state UX.
    /// </summary>
    public partial class MainPage : ContentPage
    {
        /// <summary>
        /// Injected server configuration options.
        /// </summary>
        private readonly ServerOptions _options;

        /// <summary>
        /// Final URL the WebView will navigate to.
        /// </summary>
        private readonly string _targetUrl;

        /// <summary>
        /// Initializes a new instance of <see cref="MainPage"/>.
        /// </summary>
        /// <param name="options">Server options from dependency injection.</param>
        public MainPage(IOptions<ServerOptions> options)
        {
            // Initialize visual tree from XAML
            InitializeComponent();

            // 1. Dependency injection guard
            // Avoid crashing if configuration failed to load
            if (options?.Value == null)
            {
                _options = new ServerOptions { HttpsPort = 443 }; // Safe fallback / default
            }
            else
            {
                _options = options.Value;
            }

            // 2. Configuration validation
            // Avoid malformed URLs when the configured port is invalid (0 or negative)
            int port = _options.HttpsPort > 0 ? _options.HttpsPort : 443;

            // Build target URL using the validated port
            _targetUrl = $"https://localhost:{port}";

            // 3. Safe initial load
            // Delegate web load to a separate method to contain possible errors
            LoadWebView();
        }

        /// <summary>
        /// Assigns the URL to the WebView to start navigation.
        /// Catches any synchronous exception during assignment.
        /// </summary>
        private void LoadWebView()
        {
            try
            {
                MainWebView.Source = _targetUrl;
            }
            catch (Exception ex)
            {
                // If setting the source fails (e.g. internal platform issues), show error UI immediately
                ShowErrorView();
                System.Diagnostics.Debug.WriteLine($"Critical WebView Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Fires when the WebView finishes a navigation attempt.
        /// </summary>
        /// <param name="sender">The control that raised the event (MainWebView).</param>
        /// <param name="e">Arguments containing the navigation result (success, failure, etc.).</param>
        private void OnWebViewNavigated(object sender, WebNavigatedEventArgs e)
        {
            // Check whether the page loaded successfully (HTTP success, no hard navigation errors)
            if (e.Result == WebNavigationResult.Success)
            {
                // Only show the WebView here so the user does not see a half-loaded page
                // or native OS error chrome when navigation had already failed
                MainWebView.IsVisible = true;
                ErrorLayout.IsVisible = false;
            }
            else
            {
                // On failure (e.g. server down, TLS issues), keep WebView hidden and show error layout
                ShowErrorView();
            }
        }

        /// <summary>
        /// Hides the WebView and shows the user-friendly error layout.
        /// </summary>
        private void ShowErrorView()
        {
            MainWebView.IsVisible = false;
            ErrorLayout.IsVisible = true;
        }

        /// <summary>
        /// Retry button handler on the error view; restarts navigation cleanly.
        /// </summary>
        /// <param name="sender">The button that raised the event.</param>
        /// <param name="e">Event arguments.</param>
        private void OnRetryClicked(object sender, EventArgs e)
        {
            // 1. Hide error UI to show we are handling the retry
            ErrorLayout.IsVisible = false;

            // 2. Keep WebView hidden to avoid flicker or partially rendered content during reload
            MainWebView.IsVisible = false;

            // 3. Force reload by changing Source; null first ensures the WebView observes a real change
            MainWebView.Source = null;
            MainWebView.Source = _targetUrl;
        }
    }
}
