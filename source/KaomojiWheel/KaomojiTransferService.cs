using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaomojiWheel;

public static class KaomojiTransferService
{
    public const int SchemaVersion = 1;
    public const long MaximumFileBytes = 10 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public static KaomojiTransferDocument Load(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException("找不到所选 JSON 文件。");
        var length = new FileInfo(path).Length;
        if (length > MaximumFileBytes) throw new InvalidDataException("JSON 文件不能超过 10 MB。");
        if (length == 0) throw new InvalidDataException("JSON 文件为空。");
        string json;
        try { json = StrictUtf8.GetString(File.ReadAllBytes(path)); }
        catch (DecoderFallbackException) { throw new InvalidDataException("文件不是有效的 UTF-8 编码。"); }
        return Parse(json);
    }

    public static KaomojiTransferDocument Parse(string json)
    {
        KaomojiTransferDocument? document;
        try { document = JsonSerializer.Deserialize<KaomojiTransferDocument>(json, ReadOptions); }
        catch (JsonException ex) { throw new InvalidDataException($"JSON 格式错误：{ex.Message}", ex); }
        if (document is null) throw new InvalidDataException("JSON 内容为空。");
        if (document.SchemaVersion != SchemaVersion) throw new InvalidDataException($"不支持 schemaVersion {document.SchemaVersion}，当前只支持 1。");
        if (document.Repositories is null) throw new InvalidDataException("repositories 必须是数组。");

        for (var repositoryIndex = 0; repositoryIndex < document.Repositories.Count; repositoryIndex++)
        {
            var repository = document.Repositories[repositoryIndex] ?? throw new InvalidDataException($"repositories[{repositoryIndex}] 不能为空。");
            repository.Name = (repository.Name ?? "").Trim();
            if (repository.Name.Length is < 1 or > 12) throw new InvalidDataException($"repositories[{repositoryIndex}].name 去除首尾空格后必须为 1～12 个字符。");
            if (repository.Items is null) throw new InvalidDataException($"repositories[{repositoryIndex}].items 必须是数组。");
            for (var itemIndex = 0; itemIndex < repository.Items.Count; itemIndex++)
            {
                var item = repository.Items[itemIndex];
                if (item is null) throw new InvalidDataException($"repositories[{repositoryIndex}].items[{itemIndex}] 必须是字符串。");
                item = item.Trim();
                if (item.Length is < 1 or > 100) throw new InvalidDataException($"repositories[{repositoryIndex}].items[{itemIndex}] 去除首尾空格后必须为 1～100 个字符。");
                repository.Items[itemIndex] = item;
            }
        }
        return document;
    }

    public static KaomojiImportPreview Analyze(WheelData current, KaomojiTransferDocument document)
    {
        var virtualRepositories = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in current.Repositories)
            virtualRepositories[repository.Name] = new HashSet<string>(repository.Items.Select(x => x.Text), StringComparer.Ordinal);

        var addedRepositories = 0;
        var addedItems = 0;
        var skippedDuplicates = 0;
        foreach (var source in document.Repositories)
        {
            if (!virtualRepositories.TryGetValue(source.Name, out var items))
            {
                items = new HashSet<string>(StringComparer.Ordinal);
                virtualRepositories.Add(source.Name, items);
                addedRepositories++;
            }
            foreach (var item in source.Items)
            {
                if (items.Add(item)) addedItems++;
                else skippedDuplicates++;
            }
        }
        return new(document.Repositories.Count, document.Repositories.Sum(x => x.Items.Count), addedRepositories, addedItems, skippedDuplicates);
    }

    public static KaomojiImportPreview Merge(WheelData target, KaomojiTransferDocument document)
    {
        var preview = Analyze(target, document);
        foreach (var source in document.Repositories)
        {
            var repository = target.Repositories.FirstOrDefault(x => x.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase));
            if (repository is null)
            {
                repository = new KaomojiRepository { Name = source.Name, Order = target.Repositories.Count };
                target.Repositories.Add(repository);
            }
            var existing = new HashSet<string>(repository.Items.Select(x => x.Text), StringComparer.Ordinal);
            foreach (var text in source.Items)
                if (existing.Add(text)) repository.Items.Add(new KaomojiItem { Text = text, Order = repository.Items.Count });
        }
        return preview;
    }

    public static KaomojiTransferDocument CreateExport(WheelData data) => new()
    {
        SchemaVersion = SchemaVersion,
        Repositories = data.Repositories.OrderBy(x => x.Order).Select(repository => new KaomojiTransferRepository
        {
            Name = repository.Name,
            Items = repository.Items.OrderBy(x => x.Order).Select(x => x.Text).ToList()
        }).ToList()
    };

    public static string Serialize(KaomojiTransferDocument document) => JsonSerializer.Serialize(document, WriteOptions) + Environment.NewLine;

    public static void Export(string path, WheelData data) => File.WriteAllText(path, Serialize(CreateExport(data)), new UTF8Encoding(false));
}
