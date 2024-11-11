using Claudable.Utilities;
using Claudable.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using System.IO;
using System.Text.RegularExpressions;

namespace Claudable
{
    public class WebViewManager : IDisposable
    {
        public static WebViewManager Instance { get; private set; }
        private string _baseUrl = "https://api.claude.ai";

        private readonly Regex ProjectUrl = new Regex(@"^https:\/\/claude\.ai\/project\/(?<projectId>.*?)$");
        private readonly Regex DocsUrl = new Regex(@".*?\/api\/organizations\/(?<organizationId>.*?)\/projects\/(?<projectId>.*?)\/docs(?:\/(?<artifactId>.*?))?$");
        private readonly Regex DataCollector = new Regex(@".*?/api/organizations/(?<organizationId>.*?)/projects/(?<projectId>.+?)/.*");
        private readonly WebView2 _webView;
        private string _lastVisitedUrl;
        private string _currentProjectUrl;
        private string _lastDocsResponse;
        private readonly Debouncer _reloadDebouncer;
        private readonly Debouncer _fetchDocsDebouncer;


        private string _activeProjectUuid;
        private string _activeOrganizationUuid;

        public string LastVisitedUrl => _lastVisitedUrl;
        public string CurrentProjectUrl => _currentProjectUrl;

        public event EventHandler<string> DocsReceived;
        public event EventHandler<string> ProjectChanged;
        public event EventHandler<string> ArtifactDeleted;

        public WebViewManager(WebView2 webView, string initialUrl)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));
            _lastVisitedUrl = initialUrl;
            if (Instance != null)
                throw new InvalidOperationException("Cannot instantiate more than 1 WebViewManager");
            Instance = this;
            _reloadDebouncer = new Debouncer(() =>
            {
                if (!ProjectUrl.IsMatch(LastVisitedUrl)) return;
                _webView.Reload();
            }, 1500);
            _fetchDocsDebouncer = new Debouncer(() => _ = FetchProjectDocs(), 250);
        }


        public async Task InitializeAsync()
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                "Claudable", 
                "WebView2Data"
            );
            var webView2Environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);

            await _webView.EnsureCoreWebView2Async(webView2Environment);
            _webView.Source = new Uri(_lastVisitedUrl);

            ConfigureWebViewSettings();
            SetupWebViewEventHandlers();
        }

        public async Task<ArtifactViewModel> CreateArtifact(string fileName, string content)
        {
            string url = $"https://api.claude.ai/api/organizations/{_activeOrganizationUuid}/projects/{_activeProjectUuid}/docs";

            var payload = new
            {
                file_name = fileName,
                content = content
            };

            string script = RESTCall("TrackArtifact", "POST", url, payload);

            try
            {
                string resultJson = await _webView.CoreWebView2.ExecuteScriptAsync(script);
                var artifact = JsonConvert.DeserializeObject<ArtifactViewModel>(resultJson);

                _reloadDebouncer.Debounce();
                _fetchDocsDebouncer.Debounce();
                if (artifact != null)
                {
                    artifact.ProjectUuid = _activeProjectUuid;
                    return artifact;
                }

                throw new Exception("Failed to create artifact: Invalid response");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error creating artifact: {e.Message}");
                throw;
            }
        }

        public async Task DeleteArtifact(ArtifactViewModel artifact)
        {
            string deleteUrl = $"https://api.claude.ai/api/organizations/{_activeOrganizationUuid}/projects/{artifact.ProjectUuid}/docs/{artifact.Uuid}";


            string script = RESTCall("UntrackArtifact", "DELETE", deleteUrl);

            try
            {
                var result = await _webView.CoreWebView2.ExecuteScriptAsync(script);
                Console.Write(result);
                _reloadDebouncer.Debounce();
                _fetchDocsDebouncer.Debounce();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error in delete operation: {e.Message}");
                throw;
            }
        }

        private async Task FetchProjectDocs()
        {
            string docsUrl = $"https://api.claude.ai/api/organizations/{_activeOrganizationUuid}/projects/{_activeProjectUuid}/docs";

            string script = RESTCall("fetchDocs", "GET", docsUrl);

            try
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error fetching docs: {e.Message}");
                throw;
            }
        }

        private string RESTCall(string functionName, string method, string url, object payload = null)
        {
            var includePayload = payload != null;
            string script = $@"
                async function {functionName}(url{(includePayload ? ", payload" : "")}) {{
                    try {{
                        const response = await fetch(url, {{
                            method: '{method}',
                            headers: {{
                                'authority': 'api.claude.ai',
                                'origin': 'https://claude.ai',
                                'accept': '*/*',
                                'accept-language': 'en-US,en;q=0.9',
                                'anthropic-client-sha': '',
                                'anthropic-client-version': '',
                                'content-type': 'application/json'
                            }},
                            credentials: 'include'{(includePayload
                            ? @", 
                            body: JSON.stringify(payload)"
                            : "")}
                        }});

                        if (!response.ok) {{
                            throw new Error(`HTTP error! status: ${{response.status}}`);
                        }}

                        return await response.json();
                    }} catch (error) {{
                        console.error('Error:', error);
                        throw error;
                    }}
                }}
                {functionName}('{url}'{(includePayload ? $", {JsonConvert.SerializeObject(payload)}" : "")});
            ";
            return script;

        }

        public void Navigate(string url)
        {
            _webView.Source = new Uri(url);
        }

        private void ConfigureWebViewSettings()
        {
            _webView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = true;
            _webView.CoreWebView2.Settings.IsScriptEnabled = true;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            _webView.CoreWebView2.Settings.IsPinchZoomEnabled = true;
            _webView.CoreWebView2.Settings.IsSwipeNavigationEnabled = true;
            _webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = true;
            _webView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = true;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = true;
        }

        private void SetupWebViewEventHandlers()
        {
            _webView.SourceChanged += WebView_SourceChanged;
            _webView.CoreWebView2.WebResourceResponseReceived += ProcessWebViewResponse;
            _webView.CoreWebView2.NavigationCompleted += CheckForProjectChange;
            _webView.CoreWebView2.FrameNavigationCompleted += CheckForProjectChange;
            _webView.CoreWebView2.HistoryChanged += CoreWebView2_HistoryChanged;
        }

        private void CoreWebView2_HistoryChanged(object? sender, object e) => CheckForProjectChange();

        private async void ProcessWebViewResponse(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
        {
            var url = args.Request.Uri;
            if (!url.Contains("claude")) return;

            var match = DataCollector.Match(url);
            if (match.Success)
            {
                _activeOrganizationUuid = match.Groups["organizationId"].Value;
                _activeProjectUuid = match.Groups["projectId"].Value;
            }

            var uri = new Uri(url);
            var schemeHostPath = $"https://{uri.Host}{uri.AbsolutePath}";
            switch (uri)
            {
                case var _ when DocsUrl.IsMatch(url) && args.Request.Method == "GET":
                    {
                        try
                        {
                            var response = args.Response;
                            var stream = await response.GetContentAsync();
                            using var reader = new StreamReader(stream);
                            _lastDocsResponse = reader.ReadToEnd();
                            DocsReceived?.Invoke(this, _lastDocsResponse);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                        }
                    }
                    break;
                case var _ when DocsUrl.IsMatch(url) && args.Request.Method == "DELETE":
                    {
                        var docMatch = DocsUrl.Match(url);
                        var artifactId = docMatch.Groups["artifactId"].Value;
                        ArtifactDeleted?.Invoke(this, artifactId);
                    }
                    break;
            }
        }

        private void WebView_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
        {
            _lastVisitedUrl = _webView.Source.ToString();
        }

        private async void CheckForProjectChange(object? sender, CoreWebView2NavigationCompletedEventArgs e) => CheckForProjectChange();
        private async Task CheckForProjectChange()
        {
            string url = _webView.Source.ToString();
            if (!url.Contains("claude")) return;

            var uri = new Uri(url);
            if (uri.Host == "claude.ai")
            {
                await InjectAutocompleteScript();

                if (uri.AbsolutePath.StartsWith("/project/"))
                {
                    string newProjectUrl = $"https://{uri.Host}{uri.AbsolutePath}";

                    if (newProjectUrl != _currentProjectUrl)
                    {
                        _currentProjectUrl = newProjectUrl;
                        ProjectChanged?.Invoke(this, _currentProjectUrl);
                    }
                }
            }
        }

        public void Dispose()
        {
            _reloadDebouncer?.Dispose();
            _fetchDocsDebouncer?.Dispose();
        }

        public async Task UpdateFileNameSuggestions(ProjectFolder rootFolder)
        {
            if (rootFolder == null) return;

            var fileNames = rootFolder.GetAllProjectFiles()
                .Select(f => f.Name)
                .ToList();

            var namesJson = JsonConvert.SerializeObject(fileNames);
            var script = $"if (window.claudableAutocomplete) {{ window.claudableAutocomplete.updateFileNames({namesJson}); }}";
        
            try 
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error updating file names: {ex.Message}");
            }
        }

private Task<string> InjectAutocompleteScript()
{
    string initScript = @"
        (function() {
            if (window.claudableAutocomplete) {
                return;
            }

            Object.defineProperty(window, 'claudableAutocomplete', {
                value: {
                    fileNames: [],
                    suggestionsBox: null,
                    currentWordStart: 0,
                    currentWordEnd: 0,
                    selectedIndex: -1,
                    currentTextNode: null,
                
                    createSuggestionsBox() {
                        if (!this.suggestionsBox) {
                            this.suggestionsBox = document.createElement('div');
                            this.suggestionsBox.style.position = 'fixed';
                            this.suggestionsBox.style.backgroundColor = '#1a1915';
                            this.suggestionsBox.style.border = '1px solid #3e3e39';
                            this.suggestionsBox.style.borderRadius = '8px';
                            this.suggestionsBox.style.boxShadow = '0 4px 6px rgba(0, 0, 0, 0.3)';
                            this.suggestionsBox.style.maxHeight = '200px';
                            this.suggestionsBox.style.width = 'auto';
                            this.suggestionsBox.style.minWidth = '200px';
                            this.suggestionsBox.style.overflowY = 'auto';
                            this.suggestionsBox.style.overflowX = 'hidden';
                            this.suggestionsBox.style.display = 'none';
                            this.suggestionsBox.style.zIndex = '1000';
                            document.body.appendChild(this.suggestionsBox);
                        }
                        return this.suggestionsBox;
                    },

                    findWordBoundaries(node, offset) {
                        let text = '';
                        let currentOffset = 0;
                        let start = 0;
                        let end = 0;

                        const processNode = (node) => {
                            if (node.nodeType === Node.TEXT_NODE) {
                                const nodeText = node.textContent;
                                if (currentOffset + nodeText.length >= offset) {
                                    text = nodeText;
                                    start = offset - currentOffset;
                                    while (start > 0 && !/\s/.test(text[start - 1])) {
                                        start--;
                                    }
                                    end = start;
                                    while (end < text.length && !/\s/.test(text[end])) {
                                        end++;
                                    }
                                    return true;
                                }
                                currentOffset += nodeText.length;
                            }
                            return false;
                        };

                        let currentNode = node;
                        while (currentNode && !processNode(currentNode)) {
                            currentNode = currentNode.nextSibling;
                        }

                        return {
                            text,
                            start,
                            end,
                            wordStart: currentOffset + start,
                            wordEnd: currentOffset + end,
                            node: currentNode
                        };
                    },

                    showSuggestions(matches, range) {
                        const box = this.createSuggestionsBox();
                        const rect = range.getBoundingClientRect();

                        box.style.left = `${rect.left}px`;
                        box.style.top = `${rect.bottom + 5}px`;

                        // Reset selected index
                        this.selectedIndex = -1;

                        box.innerHTML = matches
                            .map((name, index) => `
                                <div class='suggestion-item' 
                                     data-index='${index}'
                                     style='padding: 8px 12px; cursor: pointer; color: #ceccc5; background-color: #1a1915;'
                                     onmouseover='this.style.backgroundColor=""#2f2f2c""' 
                                     onmouseout='this.style.backgroundColor=""${index === this.selectedIndex ? '#2f2f2c' : '#1a1915'}""'
                                     >${name}</div>`)
                            .join('');

                        box.style.display = 'block';

                        const items = box.getElementsByClassName('suggestion-item');
                        Array.from(items).forEach(item => {
                            item.onclick = (e) => {
                                e.preventDefault();
                                e.stopPropagation();
                                this.insertSuggestion(item.textContent);
                            };
                        });
                    },

                    updateSuggestions(input) {
                        const selection = window.getSelection();
                        if (!selection.rangeCount) {
                            this.hideSuggestions();
                            return;
                        }

                        const range = selection.getRangeAt(0);
                        const { text, start, end, wordStart, wordEnd, node } = this.findWordBoundaries(
                            range.startContainer,
                            range.startOffset
                        );

                        if (!text || !node) {
                            this.hideSuggestions();
                            return;
                        }

                        this.currentTextNode = node;
                        this.currentWordStart = wordStart;
                        this.currentWordEnd = wordEnd;

                        const currentWord = text.substring(start, end);

                        if (currentWord.length < 3) {
                            this.hideSuggestions();
                            return;
                        }

                        const matches = this.fileNames.filter(name =>
                            name.toLowerCase().includes(currentWord.toLowerCase())
                        );

                        if (matches.length === 0) {
                            this.hideSuggestions();
                            return;
                        }

                        this.showSuggestions(matches, range);
                    },

                    selectSuggestion(direction) {
                        if (!this.suggestionsBox || this.suggestionsBox.style.display === 'none') return;

                        const items = this.suggestionsBox.getElementsByClassName('suggestion-item');
                        if (items.length === 0) return;

                        // Reset previous selection
                        if (this.selectedIndex >= 0 && this.selectedIndex < items.length) {
                            items[this.selectedIndex].style.backgroundColor = '#1a1915';
                        }

                        // Update index
                        if (direction === 'down') {
                            this.selectedIndex = (this.selectedIndex + 1) % items.length;
                        } else if (direction === 'up') {
                            this.selectedIndex = (this.selectedIndex - 1 + items.length) % items.length;
                        }

                        // Apply new selection
                        const selectedItem = items[this.selectedIndex];
                        selectedItem.style.backgroundColor = '#2f2f2c';
                        
                        // Ensure selected item is visible
                        selectedItem.scrollIntoView({ block: 'nearest' });
                    },

                    hideSuggestions() {
                        if (this.suggestionsBox) {
                            this.suggestionsBox.style.display = 'none';
                            this.selectedIndex = -1;
                        }
                    },

                    insertSuggestion(suggestion) {
                        if (!this.currentTextNode) return;

                        const text = this.currentTextNode.textContent;
                        const beforeText = text.substring(0, this.currentWordStart);
                        const afterText = text.substring(this.currentWordEnd);
                        this.currentTextNode.textContent = beforeText + suggestion + afterText;

                        // Set cursor position after the inserted text
                        const range = document.createRange();
                        range.setStart(this.currentTextNode, this.currentWordStart + suggestion.length);
                        range.setEnd(this.currentTextNode, this.currentWordStart + suggestion.length);
                        
                        const selection = window.getSelection();
                        selection.removeAllRanges();
                        selection.addRange(range);

                        this.hideSuggestions();
                    },

                    initialize() {
                        const observer = new MutationObserver(mutations => {
                            const editorDiv = document.querySelector('.ProseMirror');
                            if (editorDiv && !editorDiv.hasAutoComplete) {
                                editorDiv.hasAutoComplete = true;
                            
                                // Handle input events
                                editorDiv.addEventListener('input', () => {
                                    this.updateSuggestions(editorDiv);
                                });

                                // Handle keydown with maximum priority
                                const keydownHandler = (e) => {
                                    if (this.suggestionsBox?.style.display !== 'none') {
                                        if (e.key === 'Enter' && this.selectedIndex >= 0) {
                                            e.preventDefault();
                                            e.stopImmediatePropagation();
                                            const items = this.suggestionsBox.getElementsByClassName('suggestion-item');
                                            if (items[this.selectedIndex]) {
                                                this.insertSuggestion(items[this.selectedIndex].textContent);
                                            }
                                            return false;
                                        } else if (e.key === 'ArrowDown') {
                                            e.preventDefault();
                                            e.stopImmediatePropagation();
                                            this.selectSuggestion('down');
                                            return false;
                                        } else if (e.key === 'ArrowUp') {
                                            e.preventDefault();
                                            e.stopImmediatePropagation();
                                            this.selectSuggestion('up');
                                            return false;
                                        } else if (e.key === 'Escape') {
                                            this.hideSuggestions();
                                        }
                                        e.stopPropagation();
                                    }
                                };

                                // Add listeners at different levels to ensure we catch the event
                                editorDiv.addEventListener('keydown', keydownHandler, true);
                                document.addEventListener('keydown', keydownHandler, true);
                                window.addEventListener('keydown', keydownHandler, true);

                                document.addEventListener('click', (e) => {
                                    if (this.suggestionsBox && !this.suggestionsBox.contains(e.target)) {
                                        this.hideSuggestions();
                                    }
                                });
                            }
                        });

                        observer.observe(document.body, { 
                            childList: true, 
                            subtree: true 
                        });
                    },

                    updateFileNames(names) {
                        this.fileNames = names;
                        console.log('File names updated:', names);
                    }
                },
                writable: false,
                configurable: false
            });

            window.claudableAutocomplete.initialize();
        })();";

    return _webView.CoreWebView2.ExecuteScriptAsync(initScript);
}
    }
}
