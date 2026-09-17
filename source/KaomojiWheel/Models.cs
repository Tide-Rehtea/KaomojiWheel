using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KaomojiWheel;
public sealed class WheelData { public int Version { get; set; } = 1; public List<KaomojiRepository> Repositories { get; set; } = []; }
public sealed class KaomojiRepository { public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = "新仓库"; public int Order { get; set; } [JsonIgnore] public int DisplayOrder => Order + 1; public List<KaomojiItem> Items { get; set; } = []; }
public sealed class KaomojiItem { public Guid Id { get; set; } = Guid.NewGuid(); public string Text { get; set; } = ""; public int Order { get; set; } }
public sealed class WheelSettings { public string Hotkey { get; set; } = "Ctrl+Shift+Z"; public bool AutoStart { get; set; } public bool ReduceMotion { get; set; } public bool OnboardingSeen { get; set; } public bool AutoCheckUpdates { get; set; } = true; public DateTimeOffset? LastUpdateCheckUtc { get; set; } }
public sealed class UpdateManifest { public string Version { get; set; } = ""; public string PackageUrl { get; set; } = ""; public string Sha256 { get; set; } = ""; public string ReleasePageUrl { get; set; } = ""; public string ReleaseNotes { get; set; } = ""; public DateTimeOffset PublishedAtUtc { get; set; } }
public sealed class KaomojiTransferDocument
{
    [JsonRequired, JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonRequired, JsonPropertyName("repositories")] public List<KaomojiTransferRepository> Repositories { get; set; } = [];
}
public sealed class KaomojiTransferRepository
{
    [JsonRequired, JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonRequired, JsonPropertyName("items")] public List<string> Items { get; set; } = [];
}
public sealed record KaomojiImportPreview(int SourceRepositories, int SourceItems, int AddedRepositories, int AddedItems, int SkippedDuplicates);
