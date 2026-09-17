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
Check(UpdateService.CurrentVersion == "1.0.0", "正式版本号");
Check(UpdateService.IsInstallerInvocation(["--apply-update"]), "OTA 安装模式识别");

if (failures.Count > 0) { Console.Error.WriteLine($"失败 {failures.Count} 项"); return 1; }
Console.WriteLine("全部冒烟测试通过。"); return 0;
