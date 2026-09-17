using KaomojiWheel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

var failures = new List<string>();
void Check(bool condition, string name) { Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}"); if (!condition) failures.Add(name); }
void CheckThrows<T>(Action action, string name) where T : Exception { try { action(); Check(false, name); } catch (T) { Check(true, name); } }

Check(HotkeyService.TryParse("Ctrl+Shift+Z", out var mods, out var key) && mods != 0 && key == 'Z', "默认快捷键解析");
Check(!HotkeyService.TryParse("K", out _, out _), "拒绝无修饰键快捷键");
Check(!HotkeyService.TryParse("Ctrl+F13", out _, out _), "拒绝不支持的快捷键格式");

var root = Path.Combine(Path.GetTempPath(), "KaomojiWheel-SmokeTests-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(root);
    var store = new AppStore(root); store.Load();
    Check(store.Data.Repositories.Count == 0, "首次载入空仓库");
    store.Data.Repositories.Add(new KaomojiRepository { Name = "测试仓库", Order = 99, Items = [new KaomojiItem { Text = "(≧▽≦)", Order = 0 }] }); store.SaveAll();
    var reopened = new AppStore(root); reopened.Load();
    Check(reopened.Data.Repositories.Last().Name == "测试仓库", "本地 JSON 持久化");
    Check(reopened.Data.Repositories.SelectMany(x => x.Items).Any(x => x.Text.Contains('≧')), "Unicode 颜文字保持完整");
    Check(reopened.Data.Repositories.Select((x, i) => x.Order == i).All(x => x), "仓库序号连续归一化");
    Check(File.Exists(Path.Combine(root, "Data", "data.bak")), "自动生成数据备份");
    var moved = reopened.Data.Repositories[^1]; reopened.Data.Repositories.RemoveAt(reopened.Data.Repositories.Count - 1); reopened.Data.Repositories.Insert(0, moved); reopened.SaveAll();
    var reordered = new AppStore(root); reordered.Load();
    Check(reordered.Data.Repositories[0].Id == moved.Id && reordered.Data.Repositories.Select((x, i) => x.Order == i).All(x => x), "拖动顺序保存并重新载入");

    var importDocument = KaomojiTransferService.Parse("""
        {
          "schemaVersion": 1,
          "repositories": [
            { "name": " 测试仓库 ", "items": [" (≧▽≦) ", "新增一"] },
            { "name": "测试仓库", "items": ["新增一", "新增二"] },
            { "name": "卖萌", "items": ["(๑•̀ω•́๑)"] }
          ]
        }
        """);
    var preview = KaomojiTransferService.Analyze(reordered.Data, importDocument);
    Check(preview.SourceRepositories == 3 && preview.SourceItems == 5 && preview.AddedRepositories == 1 && preview.AddedItems == 3 && preview.SkippedDuplicates == 2, "导入预览与精确判重");
    var beforeImport = File.ReadAllText(Path.Combine(root, "Data", "data.json"));
    var importResult = reordered.Import(importDocument);
    var mergedRepository = reordered.Data.Repositories.First(x => x.Name == "测试仓库");
    Check(importResult.AddedItems == 3 && mergedRepository.Items.Select(x => x.Text).SequenceEqual(new[] { "(≧▽≦)", "新增一", "新增二" }), "导入追加并保持现有顺序");
    Check(reordered.Data.Repositories[^1].Name == "卖萌" && reordered.Data.Repositories[^1].Order == reordered.Data.Repositories.Count - 1, "新仓库按文件顺序追加");
    Check(File.ReadAllText(Path.Combine(root, "Data", "data.bak")) == beforeImport, "导入前自动备份数据");

    var exportPath = Path.Combine(root, "export.json"); KaomojiTransferService.Export(exportPath, reordered.Data);
    var exported = KaomojiTransferService.Load(exportPath);
    Check(exported.Repositories.Select(x => x.Name).SequenceEqual(reordered.Data.Repositories.Select(x => x.Name)) && exported.Repositories.Sum(x => x.Items.Count) == reordered.Data.Repositories.Sum(x => x.Items.Count), "导出 JSON 保持仓库与内容顺序");
    CheckThrows<InvalidDataException>(() => KaomojiTransferService.Parse("{\"schemaVersion\":2,\"repositories\":[]}"), "拒绝不支持的 JSON 版本");
    CheckThrows<InvalidDataException>(() => KaomojiTransferService.Parse("{\"schemaVersion\":1,\"repositories\":[{\"name\":\"超过十二个字符的仓库名称测试\",\"items\":[]}]}"), "拒绝越界仓库名称");
    CheckThrows<InvalidDataException>(() => KaomojiTransferService.Parse("{\"schemaVersion\":1,\"repositories\":[{\"name\":\"缺少内容\"}]}"), "拒绝缺少必需字段的 JSON");

    var starterPath = Path.Combine(AppContext.BaseDirectory, "ImportTemplates", "kaomoji-starter.json");
    Check(File.Exists(starterPath), "初始化颜文字 JSON 随构建输出");
    if (File.Exists(starterPath))
    {
        var starter = KaomojiTransferService.Load(starterPath);
        Check(starter.Repositories.Count == 11 && starter.Repositories.Sum(x => x.Items.Count) == 469, "初始化颜文字 JSON 完整有效");
        Check(starter.Repositories.Select(x => x.Name).SequenceEqual(new[] { "卖萌", "生气", "无语", "伤心", "傲娇", "开心", "惊讶", "道歉", "感谢", "安慰", "逃跑" }), "初始化仓库顺序与用户设置一致");
    }
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

var adornerLayerAvailable = false;
var wpfThread = new Thread(() =>
{
    var list = new ListBox(); var decorator = new AdornerDecorator { Child = list };
    decorator.Measure(new Size(300, 200)); decorator.Arrange(new Rect(0, 0, 300, 200)); decorator.UpdateLayout();
    adornerLayerAvailable = AdornerLayer.GetAdornerLayer(list) is not null;
});
wpfThread.SetApartmentState(ApartmentState.STA); wpfThread.Start(); wpfThread.Join();
Check(adornerLayerAvailable, "仓库列表具有拖动装饰层");
Check(UpdateService.CurrentVersion == "1.0.3", "正式版本号");
Check(UpdateService.IsInstallerInvocation(["--apply-update"]), "OTA 安装模式识别");

if (failures.Count > 0) { Console.Error.WriteLine($"失败 {failures.Count} 项"); return 1; }
Console.WriteLine("全部冒烟测试通过。"); return 0;
