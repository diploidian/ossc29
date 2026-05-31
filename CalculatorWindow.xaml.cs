using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace PlumbingCalculatorAddin
{
    public partial class CalculatorWindow : Window
    {
        private UIApplication _uiApp;
        private string _projectTitle;
        
        private CreateScheduleHandler _scheduleHandler;
        private ExternalEvent _scheduleEvent;
        
        private ShowAreaHandler _showAreaHandler;
        private ExternalEvent _showAreaEvent;

        public CalculatorWindow(UIApplication uiApp)
        {
            InitializeComponent();
            _uiApp = uiApp;
            
            GlobalSettings settings = LoadGlobalSettings();
            this.Width = settings.WindowWidth;
            this.Height = settings.WindowHeight;
            this.MinWidth = 800; // Removed fixed MaxWidth/MaxHeight so the user can resize
            this.MinHeight = 600;
            this.SizeToContent = SizeToContent.Manual;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Capture the document title safely during initialization to prevent thread context issues later
            SetProjectTitle(_uiApp.ActiveUIDocument?.Document);

            // Initialize the external event handler for Revit API transactions
            _scheduleHandler = new CreateScheduleHandler();
            _scheduleEvent = ExternalEvent.Create(_scheduleHandler);

            _showAreaHandler = new ShowAreaHandler();
            _showAreaEvent = ExternalEvent.Create(_showAreaHandler);

            InitializeBrowser();

            // Subscribe to window and Revit lifecycle events
            _uiApp.ViewActivated += OnRevitViewActivated;
            this.Activated += OnWindowActivated;
            this.Closing += OnWindowClosing;

            CleanupOldCaches();
        }

        private void SetProjectTitle(Document doc)
        {
            string rawTitle = (doc == null || string.IsNullOrWhiteSpace(doc.Title)) ? "Untitled" : doc.Title;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                rawTitle = rawTitle.Replace(c, '_');
            }
            _projectTitle = rawTitle;
        }

        private async void InitializeBrowser()
        {
            try
            {
                string userDataFolder = Path.Combine(Path.GetTempPath(), "PlumbingCalcWebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(env);

                // Enable Developer Tools so you can right-click and "Reload" or inspect elements
                webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

                // Listen for the "Sync to Revit" postMessage from your HTML
                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                
                // If loaded via Proxy, Location is empty. Fallback to hardcoded dev path.
                string addinDir = string.IsNullOrEmpty(assemblyPath) 
                    ? @"C:\Coding\Projects\Plumbing Fixture Calculator\ossc29\bin\Debug\net8.0-windows" 
                    : Path.GetDirectoryName(assemblyPath);

                // Use the absolute path to your source code so hot-reloading works perfectly
                string sourceHtmlPath = @"C:\Coding\Projects\Plumbing Fixture Calculator\ossc29\index.html";
                string htmlPath = File.Exists(sourceHtmlPath) ? sourceHtmlPath : Path.Combine(addinDir, "index.html");
                
                webView.Source = new Uri(htmlPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"WebView2 failed to initialize.\n\n{ex.Message}", "Browser Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnRevitViewActivated(object sender, ViewActivatedEventArgs e)
        {
            if (e.Document == null) return;
            
            string oldTitle = _projectTitle;
            SetProjectTitle(e.Document);

            // If the user swapped to a completely different Revit project tab
            if (oldTitle != _projectTitle)
            {
                var response = new { type = "PROJECT_CHANGED", payload = _projectTitle };
                webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(response));
            }
        }

        private void OnWindowActivated(object sender, EventArgs e)
        {
            var response = new { type = "WINDOW_FOCUSED" };
            webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(response));
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Cancel the window close and hide it instead. This completely bypasses the Revit 
            // COM interop crash during WebView2 destruction. It also keeps the Chromium 
            // process alive to be reused next time, making the add-in open instantly!
            e.Cancel = true;
            this.Hide();

            // Save final window size before hiding
            GlobalSettings settings = LoadGlobalSettings();
            settings.WindowWidth = this.Width;
            settings.WindowHeight = this.Height;
            var writeOptions = new JsonSerializerOptions { WriteIndented = true };
            SaveGlobalSettings(JsonSerializer.Serialize(settings, writeOptions));
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // This string will contain the JSON payload
            string jsonPayload = e.TryGetWebMessageAsString();

            using JsonDocument doc = JsonDocument.Parse(jsonPayload);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("action", out JsonElement actionElement))
            {
                string action = actionElement.GetString();
                
                if (action == "SAVE_CACHE")
                {
                    if (root.TryGetProperty("payload", out JsonElement payloadElement))
                    {
                        SaveToCache(payloadElement.GetRawText());
                    }
                }
                else if (action == "LOAD_CACHE")
                {
                    try
                    {
                        string cacheFile = GetProjectCacheFilePath();
                        
                        if (File.Exists(cacheFile))
                        {
                            string cacheData = File.ReadAllText(cacheFile);
                            string responseJson = $"{{\"type\":\"CACHE_LOADED\",\"payload\":{cacheData}}}";
                            webView.CoreWebView2.PostWebMessageAsJson(responseJson);
                        }
                        else
                        {
                            var response = new { type = "CACHE_LOADED", payload = (object)null };
                            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(response));
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to load cache!\n\n{ex.Message}", "Cache Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else if (action == "SAVE_GLOBAL_SETTINGS")
                {
                    if (root.TryGetProperty("payload", out JsonElement payloadElement))
                    {
                        GlobalSettings existing = LoadGlobalSettings();
                        
                        // Sync current window size as a fallback if omitted from payload
                        existing.WindowWidth = this.Width;
                        existing.WindowHeight = this.Height;

                        // Parse case-insensitively so that "col1width", "col1Width", etc. all work perfectly
                        foreach (JsonProperty prop in payloadElement.EnumerateObject())
                        {
                            string name = prop.Name.ToLower();
                            if (name == "theme") existing.Theme = prop.Value.GetString();
                            else if (name == "autosave") existing.AutoSave = prop.Value.GetBoolean();
                            else if (name == "omitkeywords") existing.OmitKeywords = prop.Value.GetString();
                            else if (name == "col1width") existing.Col1Width = prop.Value.GetDouble();
                            else if (name == "col2width") existing.Col2Width = prop.Value.GetDouble();
                            else if (name == "col3width") existing.Col3Width = prop.Value.GetDouble();
                            else if (name == "windowwidth") existing.WindowWidth = prop.Value.GetDouble();
                            else if (name == "windowheight") existing.WindowHeight = prop.Value.GetDouble();
                        }

                        var writeOptions = new JsonSerializerOptions { WriteIndented = true };
                        SaveGlobalSettings(JsonSerializer.Serialize(existing, writeOptions));
                    }
                }
                else if (action == "LOAD_GLOBAL_SETTINGS")
                {
                    try
                    {
                        GlobalSettings settings = LoadGlobalSettings();
                        var response = new { type = "GLOBAL_SETTINGS_LOADED", payload = settings };
                        webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(response));
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to load global settings!\n\n{ex.Message}", "Settings Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else if (action == "LOAD_AREAS")
                {
                    List<string> selectedSchemes = new List<string>();
                    if (root.TryGetProperty("payload", out JsonElement payloadElement) && payloadElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in payloadElement.EnumerateArray())
                        {
                            selectedSchemes.Add(item.GetString());
                        }
                    }
                    LoadAreasFromRevit(selectedSchemes);
                }
                else if (action == "GET_SCHEMES")
                {
                    // Grab all Area Plan Types (Area Schemes) from the active document
                    var schemes = new FilteredElementCollector(_uiApp.ActiveUIDocument.Document)
                        .OfClass(typeof(AreaScheme))
                        .ToElements();
                    
                    var schemeNames = new List<string>();
                    foreach (AreaScheme s in schemes)
                    {
                        if (!schemeNames.Contains(s.Name)) 
                            schemeNames.Add(s.Name);
                    }
                    
                    var response = new { type = "SCHEMES_LOADED", payload = schemeNames };
                    webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(response));
                }
                else if (action == "CREATE_SCHEDULE")
                {
                    if (root.TryGetProperty("payload", out JsonElement payloadElement))
                    {
                        _scheduleHandler.PayloadJson = payloadElement.GetRawText();
                        _scheduleEvent.Raise(); // Safely triggers the Revit API thread!
                    }
                }
                else if (action == "SHOW_AREA")
                {
                    if (root.TryGetProperty("payload", out JsonElement payloadElement))
                    {
                        _showAreaHandler.AreaUniqueId = payloadElement.GetString();
                        _showAreaEvent.Raise();
                    }
                }
            }
            else
            {
                // Legacy direct sync fallback
                SyncData data = JsonSerializer.Deserialize<SyncData>(jsonPayload);
                MessageBox.Show($"Successfully parsed data!\n\nTotal Spaces: {data?.Spaces?.Count}\nProposed Male WC: {data?.Proposed?.MaleWC}", "Sync Initialized");
            }
        }

        private void LoadAreasFromRevit(List<string> selectedSchemes)
        {
            // 1. Load global settings to get Omit Keywords
            GlobalSettings settings = LoadGlobalSettings();
            List<string> omitKeywords = new List<string>();
            if (!string.IsNullOrWhiteSpace(settings?.OmitKeywords))
            {
                omitKeywords = settings.OmitKeywords
                    .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim().ToLower())
                    .Where(k => !string.IsNullOrEmpty(k)).ToList();
            }

            Document doc = _uiApp.ActiveUIDocument.Document;
            var areas = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Areas)
                .WhereElementIsNotElementType()
                .ToElements();

            var areaList = new List<object>();
            foreach (Area area in areas)
            {
                if (area.Area == 0) continue; // Skip unplaced areas

                // If the user selected specific plan types, filter out the ones that don't match
                if (selectedSchemes != null && selectedSchemes.Count > 0)
                {
                    if (area.AreaScheme == null || !selectedSchemes.Contains(area.AreaScheme.Name))
                        continue;
                }

                string areaName = area.Name ?? area.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "Unnamed";
                string comments = area.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                
                // 2. Filter out keywords
                bool skipArea = false;
                foreach (string kw in omitKeywords)
                {
                    if (areaName.ToLower().Contains(kw) || comments.ToLower().Contains(kw))
                    {
                        skipArea = true; break;
                    }
                }
                if (skipArea) continue;

                Parameter occTypeParam = area.LookupParameter("Code-Occupancy Type");
                Parameter olfParam = area.LookupParameter("Code-OLF");
                
                double areaSqFt = area.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() ?? 0;

                // Extract OLF as a number to calculate Total Occupancy
                double olfValue = 0;
                if (olfParam != null && olfParam.HasValue)
                {
                    switch (olfParam.StorageType)
                    {
                        case StorageType.Double:
                            olfValue = olfParam.AsDouble();
                            break;
                        case StorageType.Integer:
                            olfValue = olfParam.AsInteger();
                            break;
                        case StorageType.String:
                            double.TryParse(olfParam.AsString(), out olfValue);
                            break;
                    }
                }

                // Calculate Total Occupancy: roundup(Area / OLF)
                int totalOcc = 0;
                if (olfValue > 0)
                {
                    totalOcc = (int)Math.Ceiling(areaSqFt / olfValue);
                }

                areaList.Add(new
                {
                    id = area.UniqueId,
                    name = areaName,
                    area = areaSqFt,
                    occupancyType = occTypeParam?.AsString() ?? "Unassigned",
                    olf = olfParam?.AsValueString() ?? olfParam?.AsString() ?? "Varies",
                    totalOccupancy = totalOcc,
                    levelElevation = area.Level?.Elevation ?? 0.0,
                    levelName = area.Level?.Name ?? "Level"
                });
            }

            var response = new { type = "AREAS_LOADED", payload = areaList };
            webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(response));
        }

        private string GetProjectCacheFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string cacheDir = Path.Combine(appData, "PlumbingCalculatorAddin", "ProjectCache");
            Directory.CreateDirectory(cacheDir);

            return Path.Combine(cacheDir, $"{_projectTitle}.json");
        }

        private void SaveToCache(string jsonPayload)
        {
            try
            {
                string cacheFile = GetProjectCacheFilePath();
                File.WriteAllText(cacheFile, jsonPayload);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving cache to {GetProjectCacheFilePath()}:\n\n{ex.Message}", "Cache Save Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CleanupOldCaches()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string cacheDir = Path.Combine(appData, "PlumbingCalculatorAddin", "ProjectCache");
                if (Directory.Exists(cacheDir))
                {
                    var files = Directory.GetFiles(cacheDir, "*.json");
                    DateTime cutoff = DateTime.Now.AddMonths(-6);
                    foreach (var file in files)
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                }
            }
            catch { /* Fail silently */ }
        }

        private string GetGlobalConfigFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDir = Path.Combine(appData, "PlumbingCalculatorAddin");
            Directory.CreateDirectory(configDir);
            return Path.Combine(configDir, "config.json");
        }

        private GlobalSettings LoadGlobalSettings()
        {
            string configFile = GetGlobalConfigFilePath();
            if (File.Exists(configFile))
            {
                try
                {
                    string json = File.ReadAllText(configFile);
                    var readOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<GlobalSettings>(json, readOptions) ?? new GlobalSettings();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error reading config.json:\n\n{ex.Message}", "Config Read Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // If the file doesn't exist or failed to read, create it with default settings
            var defaultSettings = new GlobalSettings();
            var writeOptions = new JsonSerializerOptions { WriteIndented = true };
            SaveGlobalSettings(JsonSerializer.Serialize(defaultSettings, writeOptions));
            return defaultSettings;
        }

        private void SaveGlobalSettings(string jsonPayload)
        {
            try 
            { 
                File.WriteAllText(GetGlobalConfigFilePath(), jsonPayload); 
            } 
            catch (Exception ex) 
            { 
                MessageBox.Show($"Failed to save settings to config.json:\n\n{ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Warning); 
            }
        }
    }

    /// <summary>
    /// Handles all Revit document modifications safely on the main Revit API thread.
    /// </summary>
    public class CreateScheduleHandler : IExternalEventHandler
    {
        public string PayloadJson { get; set; }

        public void Execute(UIApplication app)
        {
            if (string.IsNullOrEmpty(PayloadJson)) return;

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return;

            try
            {
                using (Transaction tx = new Transaction(doc, "Generate Plumbing Schedule"))
                {
                    tx.Start();

                    // 1. Check and Load Families (Change these strings if your files are named differently!)
                    Family headerFam = LoadFamilyIfNeeded(doc, "Plumbing_Header");
                    Family rowFam = LoadFamilyIfNeeded(doc, "Plumbing_Row");
                    Family footerFam = LoadFamilyIfNeeded(doc, "Plumbing_Footer");

                    // 2. Get or Create Drafting View
                    ViewDrafting scheduleView = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewDrafting))
                        .Cast<ViewDrafting>()
                        .FirstOrDefault(v => v.Name == "PLUMBING FIXTURE CALCS");

                    if (scheduleView == null)
                    {
                        ViewFamilyType draftingType = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.Drafting);

                        if (draftingType != null)
                        {
                            scheduleView = ViewDrafting.Create(doc, draftingType.Id);
                            scheduleView.Name = "PLUMBING FIXTURE CALCS";
                        }
                    }
                    else
                    {
                        // Clear existing schedule elements from the view if we are updating it
                        var elementsToDelete = new FilteredElementCollector(doc, scheduleView.Id)
                            .OfClass(typeof(FamilyInstance))
                            .ToElementIds();
                        if (elementsToDelete.Count > 0)
                            doc.Delete(elementsToDelete);
                    }
                        
                        // Force the Drafting View scale to 12" = 1'-0" (1:1 scale factor)
                        scheduleView.Scale = 1;

                    // 3. Place the Families
                    FamilySymbol headerSym = GetSymbol(doc, headerFam);
                    FamilySymbol rowSym = GetSymbol(doc, rowFam);
                    FamilySymbol footerSym = GetSymbol(doc, footerFam);

                    if (headerSym != null && rowSym != null && footerSym != null)
                    {
                        // Shifted down an extra 1 1/32" (total 2.0625") based on your request!
                        double dropAfterHeader = 0 / 12.0; 
                        double rowHeight = 0.34375 / 12.0;
                        double footerXOffset = 3.75 / 12.0;

                        XYZ currentPos = XYZ.Zero;
                        
                        // Parse JSON Data
                        using JsonDocument jsonDoc = JsonDocument.Parse(PayloadJson);
                        JsonElement root = jsonDoc.RootElement;
                        JsonElement spacesArray = root.GetProperty("spaces");
                        JsonElement totals = root.GetProperty("totals");
                        JsonElement proposed = root.GetProperty("proposed");
                        JsonElement unroundedTotals = root.GetProperty("unroundedTotals");

                        // Place Header
                        doc.Create.NewFamilyInstance(currentPos, headerSym, scheduleView);
                        
                        // Move down by the dropAfterHeader amount
                        currentPos = new XYZ(currentPos.X, currentPos.Y - dropAfterHeader, currentPos.Z);
                        
                        // Loop through every space and place a row
                        foreach (JsonElement space in spacesArray.EnumerateArray())
                        {
                            FamilyInstance rowInst = doc.Create.NewFamilyInstance(currentPos, rowSym, scheduleView);
                            
                            SetParameter(rowInst, "Use", space.GetProperty("Use").GetString());
                            SetParameter(rowInst, "Ratio_MWC", space.GetProperty("Ratio_MWC").GetString());
                            SetParameter(rowInst, "Ratio_FWC", space.GetProperty("Ratio_FWC").GetString());
                            SetParameter(rowInst, "Ratio_MLav", space.GetProperty("Ratio_MLav").GetString());
                            SetParameter(rowInst, "Ratio_FLav", space.GetProperty("Ratio_FLav").GetString());
                            SetParameter(rowInst, "Ratio_DF", space.GetProperty("Ratio_DF").GetString());

                            SetParameter(rowInst, "Actual_MWC", space.GetProperty("Actual_MWC").GetDouble().ToString("0.00"));
                            SetParameter(rowInst, "Actual_FWC", space.GetProperty("Actual_FWC").GetDouble().ToString("0.00"));
                            SetParameter(rowInst, "Actual_MLav", space.GetProperty("Actual_MLav").GetDouble().ToString("0.00"));
                            SetParameter(rowInst, "Actual_FLav", space.GetProperty("Actual_FLav").GetDouble().ToString("0.00"));
                            
                            JsonElement dfElement = space.GetProperty("Actual_DF");
                            if (dfElement.ValueKind == JsonValueKind.String)
                            {
                                SetParameter(rowInst, "Actual_DF", dfElement.GetString());
                            }
                            else
                            {
                                double actDf = dfElement.GetDouble();
                                SetParameter(rowInst, "Actual_DF", actDf > 0 ? actDf.ToString("0") : "-");
                            }

                            // Move down by the exact height of the Row for the next one
                            currentPos = new XYZ(currentPos.X, currentPos.Y - rowHeight, currentPos.Z);
                        }

                        // Shift right by 3.75 inches for Footer (we already shifted down at the end of the loop)
                        currentPos = new XYZ(currentPos.X + footerXOffset, currentPos.Y, currentPos.Z);
                        
                        // Place Footer
                        FamilyInstance footerInst = doc.Create.NewFamilyInstance(currentPos, footerSym, scheduleView);
                        
                        // Footer: Unrounded Totals
                        SetParameter(footerInst, "Total_MWC", unroundedTotals.GetProperty("mWC").GetDouble().ToString("0.00"));
                        SetParameter(footerInst, "Total_FWC", unroundedTotals.GetProperty("fWC").GetDouble().ToString("0.00"));
                        SetParameter(footerInst, "Total_MLav", unroundedTotals.GetProperty("mLav").GetDouble().ToString("0.00"));
                        SetParameter(footerInst, "Total_FLav", unroundedTotals.GetProperty("fLav").GetDouble().ToString("0.00"));
                        double totDf = unroundedTotals.GetProperty("df").GetDouble();
                        SetParameter(footerInst, "Total_DF", totDf > 0 ? totDf.ToString("0") : "-");
                        
                        // Footer: Required Totals (Rounded up)
                        SetParameter(footerInst, "Req_MWC", totals.GetProperty("mWC").GetDouble().ToString("0"));
                        SetParameter(footerInst, "Req_FWC", totals.GetProperty("fWC").GetDouble().ToString("0"));
                        SetParameter(footerInst, "Req_MLav", totals.GetProperty("mLav").GetDouble().ToString("0"));
                        SetParameter(footerInst, "Req_FLav", totals.GetProperty("fLav").GetDouble().ToString("0"));
                        double reqDf = totals.GetProperty("df").GetDouble();
                        SetParameter(footerInst, "Req_DF", reqDf > 0 ? reqDf.ToString("0") : "-");
                        
                        // Footer: Proposed & Unisex
                        SetParameter(footerInst, "Prop_MWC", proposed.GetProperty("mWC").GetDouble().ToString("0"));
                        SetParameter(footerInst, "Prop_FWC", proposed.GetProperty("fWC").GetDouble().ToString("0"));
                        SetParameter(footerInst, "Prop_MLav", proposed.GetProperty("mLav").GetDouble().ToString("0"));
                        SetParameter(footerInst, "Prop_FLav", proposed.GetProperty("fLav").GetDouble().ToString("0"));
                        double propDf = proposed.GetProperty("df").GetDouble();
                        SetParameter(footerInst, "Prop_DF", propDf > 0 ? propDf.ToString("0") : "-");
                        
                        SetParameter(footerInst, "Unisex_WC", proposed.GetProperty("uWC").GetDouble().ToString("0"));
                        SetParameter(footerInst, "Unisex_Lav", proposed.GetProperty("uLav").GetDouble().ToString("0"));
                    }

                    tx.Commit();
                    
                    // Open the view so the user can see it instantly!
                    if (scheduleView != null)
                    {
                        app.ActiveUIDocument.ActiveView = scheduleView;
                    }
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Schedule Error", $"Failed to create schedule:\n\n{ex.Message}");
            }
        }

        public string GetName() => "Create Schedule Handler";

        // Helper method to safely write strings into the family parameters
        private void SetParameter(FamilyInstance inst, string paramName, string value)
        {
            Parameter param = inst.LookupParameter(paramName);
            if (param != null && !param.IsReadOnly)
            {
                param.Set(value ?? "");
            }
        }

        private FamilySymbol GetSymbol(Document doc, Family family)
        {
            var symbolIds = family.GetFamilySymbolIds();
            if (symbolIds.Count > 0)
            {
                var symbol = doc.GetElement(symbolIds.First()) as FamilySymbol;
                if (symbol != null && !symbol.IsActive)
                {
                    symbol.Activate();
                }
                return symbol;
            }
            return null;
        }

        private Family LoadFamilyIfNeeded(Document doc, string familyName)
        {
            Family family = new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => f.Name == familyName);

            if (family == null)
            {
                // For now, point directly to the project root folder
                string rootDir = @"C:\Coding\Projects\Plumbing Fixture Calculator\ossc29";
                string rfaPath = Path.Combine(rootDir, "RFA", familyName + ".rfa");
                
                if (File.Exists(rfaPath))
                {
                    doc.LoadFamily(rfaPath, out family);
                }
                else
                {
                    throw new FileNotFoundException($"Could not find family file:\n{rfaPath}\n\nPlease ensure your families are placed in the RFA folder.");
                }
            }
            return family;
        }
    }

    /// <summary>
    /// Finds an area element, activates a view where it is visible, and selects it.
    /// </summary>
    public class ShowAreaHandler : IExternalEventHandler
    {
        public string AreaUniqueId { get; set; }

        public void Execute(UIApplication app)
        {
            if (string.IsNullOrEmpty(AreaUniqueId)) return;

            Document doc = app.ActiveUIDocument?.Document;
            if (doc == null) return;

            try
            {
                Element element = doc.GetElement(AreaUniqueId);
                if (element != null)
                {
                    app.ActiveUIDocument.Selection.SetElementIds(new List<ElementId> { element.Id });
                    app.ActiveUIDocument.ShowElements(element.Id); // Automatically opens a visible view and zooms!
                }
            }
            catch { /* Ignore if a valid view cannot be found by Revit */ }
        }
        public string GetName() => "Show Area Handler";
    }
}