using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PlumbingCalculatorAddin
{
    public class GlobalSettings
    {
        [JsonPropertyName("theme")] public string Theme { get; set; } = "light";
        [JsonPropertyName("autoSave")] public bool AutoSave { get; set; } = true;
        [JsonPropertyName("omitKeywords")] public string OmitKeywords { get; set; } = "";
        [JsonPropertyName("windowWidth")] public double WindowWidth { get; set; } = 2380;
        [JsonPropertyName("windowHeight")] public double WindowHeight { get; set; } = 1180;
        [JsonPropertyName("col1Width")] public double Col1Width { get; set; } = 670;
        [JsonPropertyName("col2Width")] public double Col2Width { get; set; } = 670;
        [JsonPropertyName("col3Width")] public double Col3Width { get; set; } = 1040;
    }

    public class SyncData
    {
        [JsonPropertyName("spaces")] public List<SpaceData> Spaces { get; set; }
        [JsonPropertyName("totals")] public TotalsData Totals { get; set; }
        [JsonPropertyName("proposed")] public ProposedData Proposed { get; set; }
    }

    public class SpaceData
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; }
        [JsonPropertyName("load")] public int Load { get; set; }
    }

    public class TotalsData
    {
        [JsonPropertyName("mWC")] public int MaleWC { get; set; }
        [JsonPropertyName("fWC")] public int FemaleWC { get; set; }
        [JsonPropertyName("mLav")] public int MaleLav { get; set; }
        [JsonPropertyName("fLav")] public int FemaleLav { get; set; }
        [JsonPropertyName("df")] public int DrinkingFountains { get; set; }
    }

    public class ProposedData
    {
        [JsonPropertyName("mWC")] public int MaleWC { get; set; }
        [JsonPropertyName("fWC")] public int FemaleWC { get; set; }
        [JsonPropertyName("mLav")] public int MaleLav { get; set; }
        [JsonPropertyName("fLav")] public int FemaleLav { get; set; }
        [JsonPropertyName("df")] public int DrinkingFountains { get; set; }
        [JsonPropertyName("uWC")] public int UnisexWC { get; set; }
        [JsonPropertyName("uLav")] public int UnisexLav { get; set; }
    }

    public class RevitArea
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("area")] public double Area { get; set; }
        [JsonPropertyName("occupancyType")] public string OccupancyType { get; set; }
        [JsonPropertyName("olf")] public string Olf { get; set; }
        [JsonPropertyName("totalOccupancy")] public int TotalOccupancy { get; set; }
    }
}