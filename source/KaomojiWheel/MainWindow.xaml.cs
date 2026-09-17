using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using IODirectory = System.IO.Directory;
using IOPath = System.IO.Path;

namespace KaomojiWheel;

public partial class MainWindow : Window, IDisposable
{
    private const double Orb = 72, Gap = 16, Plus = 48, Pager = 42, Safety = 7;
    private const double RingRadius = Orb + Gap;
    private const double CenterEdgeInset = Orb / 2 + Safety;
    private const double PagerGap = 8, PagerTextWidth = 62, SettingsSize = 36;
    private readonly AppStore _store;
    private HotkeyService? _hotkey;
    private IntPtr _previousWindow;
    private int _page;
    private int _capacity = 6;
    private Point _center;
    private Point _displayCenter;
    private readonly List<Point> _slots = [];
    private KaomojiRepository? _currentRepo;
    private string _modalMode = "";
    private object? _modalTarget;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _updateTimer;
    private readonly UpdateService _updateService = new();
    private UpdateManifest? _availableUpdate;
    private TextBlock? _updateStatus;
    private TextBlock? _updateNotes;
    private Button? _checkUpdateButton;
    private Button? _installUpdateButton;
    private bool _checkingUpdate;
    private string? _notifiedUpdateVersion;
    private ListBox? _orderList;
    private Point _dragStart;
    private OrderDragAdorner? _orderDragAdorner;
    private AdornerLayer? _orderAdornerLayer;
    private ListBoxItem? _dragSourceContainer;
    private ListBoxItem? _dropTargetContainer;
    private int? _pendingInsertionIndex;
    private DateTime _lastDragScroll = DateTime.MinValue;

    public MainWindow(AppStore store)
    {
        InitializeComponent(); _store = store;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesIfDueAsync();
    }

    public event Action<UpdateManifest>? UpdateAvailable;
    public void StartAutomaticUpdateChecks(){if(!_store.Settings.AutoCheckUpdates)return;_updateTimer.Start();_ = CheckForUpdatesIfDueAsync();}

    public void InitializeHotkey()
    {
        new WindowInteropHelper(this).EnsureHandle();
        _hotkey = new HotkeyService(this, ShowWheel);
        if (!_hotkey.Register(_store.Settings.Hotkey)) ShowToast("默认快捷键不可用，请从托盘打开并修改");
    }

    public void ShowWheel()
    {
        if (IsVisible) { HideOverlay(); return; }
        // Keep the whole layered HWND transparent until WPF and DWM have both
        // committed the newly positioned frame. Hiding only Root is not enough:
        // DWM can briefly reuse the previous surface when a hidden HWND returns.
        Opacity = 0; Root.Visibility = Visibility.Hidden; Root.Opacity = 0; WheelCanvas.Children.Clear();
        _previousWindow = GetForegroundWindow();
        var cursorPx = Forms.Cursor.Position; var screen = Forms.Screen.FromPoint(cursorPx);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = screen.WorkingArea.Left / scale; Top = screen.WorkingArea.Top / scale;
        Width = screen.WorkingArea.Width / scale; Height = screen.WorkingArea.Height / scale;
        _displayCenter = new Point((screen.Bounds.Left + screen.Bounds.Width / 2d) / scale - Left, (screen.Bounds.Top + screen.Bounds.Height / 2d) / scale - Top);
        _center = new Point(Math.Clamp(cursorPx.X / scale - Left, CenterEdgeInset, Width - CenterEdgeInset), Math.Clamp(cursorPx.Y / scale - Top, CenterEdgeInset, Height - CenterEdgeInset));
        _page = 0; RepositoryPanel.Visibility = SettingsPanel.Visibility = InputModal.Visibility = OnboardingPanel.Visibility = Visibility.Collapsed;
        WheelCanvas.Visibility = Visibility.Visible; BuildWheel(false); Show(); UpdateLayout();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            Root.Visibility = Visibility.Visible; Root.Opacity = 1; UpdateLayout();
            Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            try { DwmFlush(); } catch (DllNotFoundException) { }
            Opacity = 1; AnimateWheel(); Activate();
        }));
    }

    public void ShowOnboarding()
    {
        var screen = Forms.Screen.PrimaryScreen!; var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = screen.WorkingArea.Left / scale; Top = screen.WorkingArea.Top / scale; Width = screen.WorkingArea.Width / scale; Height = screen.WorkingArea.Height / scale;
        _displayCenter = new Point((screen.Bounds.Left + screen.Bounds.Width / 2d) / scale - Left, (screen.Bounds.Top + screen.Bounds.Height / 2d) / scale - Top);_center = new Point(Width / 2, Height / 2); Opacity = 1; Root.Visibility = Visibility.Visible; Root.Opacity = 1; WheelCanvas.Visibility = Visibility.Collapsed; OnboardingPanel.Visibility = Visibility.Visible; Center(OnboardingPanel); Show(); Activate();
    }

    private void BuildWheel(bool animate = true)
    {
        WheelCanvas.Children.Clear(); _slots.Clear();
        for (var i = 0; i < 6; i++)
        {
            var angle = (-90 + i * 60) * Math.PI / 180;
            var p = new Point(_center.X + Math.Cos(angle) * RingRadius, _center.Y + Math.Sin(angle) * RingRadius);
            if (FitsCircle(p, Orb)) _slots.Add(p);
        }
        // A complete ring starts at 12 o'clock and increases clockwise. Partial
        // edge layouts retain the previously agreed top-to-bottom row filling.
        if (_slots.Count < 6)
            _slots.Sort((a,b) => Math.Abs(a.Y-b.Y) < 8 ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        _capacity = _slots.Count;
        var pageCount = _capacity == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(_store.Data.Repositories.Count / (double)_capacity));
        _page = Math.Clamp(_page, 0, pageCount - 1);
        if (_capacity > 0)
        {
            var repos = _store.Data.Repositories.Skip(_page * _capacity).Take(_capacity).ToList();
            for (var i = 0; i < repos.Count; i++) AddRepositoryOrb(repos[i], _slots[i], i);
        }
        AddCenterPlus(); AddPager(pageCount); if (animate) AnimateWheel();
    }

    private bool FitsCircle(Point p, double size) => p.X-size/2 >= Safety && p.X+size/2 <= Width-Safety && p.Y-size/2 >= Safety && p.Y+size/2 <= Height-Safety;

    private void AddRepositoryOrb(KaomojiRepository repo, Point p, int colorIndex)
    {
        var colors = new[] { "#77849A", "#867D96", "#748E8B", "#927E84", "#74869A", "#928774" };
        var button = CircleButton(repo.Name, Orb, colors[(repo.Order + colorIndex) % colors.Length]);
        button.Tag = repo; button.Click += (_, _) => OpenRepository(repo);
        var menu = StyledContextMenu();
        var rename = StyledMenuItem("重命名"); rename.Click += (_, _) => OpenInput("renameRepo", repo, "重命名仓库", repo.Name, 12);
        var delete = StyledMenuItem("删除", true); delete.Click += (_, _) => DeleteRepository(repo);
        menu.Items.Add(rename); menu.Items.Add(delete); button.ContextMenu = menu;
        Place(button, p, Orb, Orb); WheelCanvas.Children.Add(button);
    }

    private void AddCenterPlus()
    {
        var icon = new Path { Data = Geometry.Parse("M8,2 L8,14 M2,8 L14,8"), Stroke = Brushes.White, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 16, Height = 16, Stretch = Stretch.None, SnapsToDevicePixels = true };
        var b = CircleButton(icon, Plus, "#788397"); b.Padding = new Thickness(0); b.Click += (_, _) => OpenInput("addRepo", null, "创建仓库", "", 12); Place(b, _center, Plus, Plus); WheelCanvas.Children.Add(b);
    }

    private void AddPager(int pageCount)
    {
        var placement = FindPagerPlacement();
        var group = new StackPanel { Orientation = Orientation.Horizontal, Height = Pager, VerticalAlignment = VerticalAlignment.Center };
        var settings = IconCircleButton(SettingsGeometry(), SettingsSize, "设置"); settings.Click += (_,_) => OpenSettings();
        var prev = IconCircleButton(ArrowGeometry(false), Pager, "上一页"); prev.IsEnabled = _page > 0; prev.Click += (_,_) => ChangePage(-1);
        var next = IconCircleButton(ArrowGeometry(true), Pager, "下一页"); next.IsEnabled = _page < pageCount - 1; next.Click += (_,_) => ChangePage(1);
        var pageBox = new Border
        {
            Width = PagerTextWidth, Height = Pager, Margin = new Thickness(PagerGap / 2, 0, PagerGap / 2, 0),
            Background = new SolidColorBrush(Color.FromArgb(132, 25, 29, 38)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 145, 153, 170)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Pager / 2),
            Child = new TextBlock { Text = $"{_page + 1} / {pageCount}", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center }
        };
        if (placement.SettingsOnLeft) { group.Children.Add(settings); settings.Margin = new Thickness(0, 3, PagerGap, 3); }
        group.Children.Add(prev); group.Children.Add(pageBox); group.Children.Add(next);
        if (!placement.SettingsOnLeft) { settings.Margin = new Thickness(PagerGap, 3, 0, 3); group.Children.Add(settings); }
        Canvas.SetLeft(group, placement.Left); Canvas.SetTop(group, placement.Top); WheelCanvas.Children.Add(group);
    }

    private (double Left, double Top, bool SettingsOnLeft) FindPagerPlacement()
    {
        var wheelExtent = RingRadius + Orb / 2;
        var totalWidth = SettingsSize + PagerGap + Pager * 2 + PagerTextWidth + PagerGap;
        // For top/bottom placement, align the page-number capsule itself with
        // the wheel center. The settings button stays to the right and must not
        // shift that visual axis.
        var pageCenterOffset = Pager + PagerGap / 2 + PagerTextWidth / 2;
        var horizontalLeft = _center.X - pageCenterOffset;
        var bottom = _center.Y + wheelExtent + Gap;
        if (FitsRect(horizontalLeft, bottom, totalWidth, Pager)) return (horizontalLeft, bottom, false);
        var top = _center.Y - wheelExtent - Gap - Pager;
        if (FitsRect(horizontalLeft, top, totalWidth, Pager)) return (horizontalLeft, top, false);

        // At the left edge the free side is right; at the right edge it is left.
        var right = _center.X + wheelExtent + Gap;
        var sideTop = Math.Clamp(_center.Y - Pager / 2, Safety, Height - Safety - Pager);
        if (FitsRect(right, sideTop, totalWidth, Pager)) return (right, sideTop, false);
        var left = _center.X - wheelExtent - Gap - totalWidth;
        if (FitsRect(left, sideTop, totalWidth, Pager)) return (left, sideTop, true);

        // Very small work areas: keep the complete group visible at the nearest safe point.
        return (Math.Clamp(horizontalLeft, Safety, Math.Max(Safety, Width - Safety - totalWidth)), Math.Clamp(bottom, Safety, Math.Max(Safety, Height - Safety - Pager)), horizontalLeft > Width / 2);
    }

    private bool FitsRect(double left, double top, double width, double height) => left >= Safety && top >= Safety && left + width <= Width - Safety && top + height <= Height - Safety;

    private Button IconCircleButton(Geometry geometry, double size, string tooltip)
    {
        var filled = tooltip == "设置";
        var icon = new Path { Data = geometry, Fill = filled ? Brushes.White : null, Stroke = filled ? null : Brushes.White, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform, Width = size * .38, Height = size * .38 };
        var button = CircleButton(icon, size, "#737C8C"); button.ToolTip = tooltip; button.Padding = new Thickness(0); return button;
    }

    private static Geometry ArrowGeometry(bool next) => Geometry.Parse(next ? "M 2,1 L 8,7 L 2,13" : "M 8,1 L 2,7 L 8,13");
    private static Geometry SettingsGeometry() => Geometry.Parse("F0 M12,2 L14.87,5.07 L19.07,4.93 L18.93,9.13 L22,12 L18.93,14.87 L19.07,19.07 L14.87,18.93 L12,22 L9.13,18.93 L4.93,19.07 L5.07,14.87 L2,12 L5.07,9.13 L4.93,4.93 L9.13,5.07 Z M12,8.4 A3.6,3.6 0 1 0 12,15.6 A3.6,3.6 0 1 0 12,8.4 Z");

    private Button CircleButton(object content,double size,string color)
    {
        var accent=(Color)ColorConverter.ConvertFromString(color);
        var background=new SolidColorBrush(Color.FromArgb(112,24,28,36));
        var border=new SolidColorBrush(Color.FromArgb(205,accent.R,accent.G,accent.B));
        var b=new Button{Content=content,Width=size,Height=size,Foreground=new SolidColorBrush(Color.FromRgb(237,239,244)),FontSize=14,FontWeight=FontWeights.SemiBold,Background=background,BorderBrush=border,BorderThickness=new Thickness(1.7),Padding=new Thickness(6)};
        b.Template=(ControlTemplate)FindResource("CircleTemplate"); return b;
    }

    private void ChangePage(int delta) { _page += delta; BuildWheel(); }
    private void AnimateWheel()
    {
        var duration=TimeSpan.FromMilliseconds(_store.Settings.ReduceMotion?80:220);
        foreach(UIElement child in WheelCanvas.Children) { child.Opacity=0; child.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,duration)); }
    }

    private void OpenRepository(KaomojiRepository repo)
    {
        _currentRepo=repo; WheelCanvas.Visibility=Visibility.Collapsed; SettingsPanel.Visibility=Visibility.Collapsed; RepositoryPanel.Visibility=Visibility.Visible; RepositoryTitle.Text=repo.Name; RenderItems(); Center(RepositoryPanel);
    }
    private void RenderItems()
    {
        ItemsPanel.Children.Clear(); if(_currentRepo is null)return; RandomButton.IsEnabled=_currentRepo.Items.Count>0;
        foreach(var item in _currentRepo.Items.OrderBy(x=>x.Order))
        {
            var b=new Button{Content=new TextBlock{Text=item.Text,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center},Style=(Style)FindResource("GlassButton"),Margin=new Thickness(4),MinHeight=44,Width=146,Tag=item};
            b.Click += async (_,_)=>await CopyAndToast(item.Text,b);
            var menu=StyledContextMenu(); var edit=StyledMenuItem("编辑"); edit.Click+=(_,_)=>OpenInput("editItem",item,"编辑颜文字",item.Text,100);
            var move=StyledMenuItem("移动到其他仓库"); foreach(var target in _store.Data.Repositories.Where(x=>x.Id!=_currentRepo.Id)){var mi=StyledMenuItem(target.Name);mi.Tag=target;mi.Click+=(_,_)=>MoveItem(item,target);move.Items.Add(mi);} var del=StyledMenuItem("删除",true);del.Click+=(_,_)=>DeleteItem(item);
            menu.Items.Add(edit);menu.Items.Add(move);menu.Items.Add(del);b.ContextMenu=menu;ItemsPanel.Children.Add(b);
        }
    }

    private ContextMenu StyledContextMenu() => new() { Style = (Style)FindResource("GlassContextMenu") };
    private MenuItem StyledMenuItem(string header, bool destructive = false) => new()
    {
        Header = header,
        Style = (Style)FindResource("GlassMenuItem"),
        Foreground = destructive ? new SolidColorBrush(Color.FromRgb(242, 158, 166)) : new SolidColorBrush(Color.FromRgb(240, 242, 246))
    };

    private async Task CopyAndToast(string text, UIElement? highlight=null)
    {
        var ok=await ClipboardService.CopyAsync(text); ShowToast(ok?$"已复制：{text}":"复制失败，请重试",!ok); if(highlight is not null){highlight.Opacity=.55; await Task.Delay(120); highlight.Opacity=1;}
    }
    private async void Random_Click(object sender,RoutedEventArgs e) { if(_currentRepo?.Items.Count>0){var item=_currentRepo.Items[Random.Shared.Next(_currentRepo.Items.Count)];await CopyAndToast(item.Text);} }
    private void AddItem_Click(object sender,RoutedEventArgs e)=>OpenInput("addItem",null,"添加颜文字","",100);
    private void BackToWheel_Click(object sender,RoutedEventArgs e){InputModal.Visibility=RepositoryPanel.Visibility=SettingsPanel.Visibility=Visibility.Collapsed;WheelCanvas.Visibility=Visibility.Visible;BuildWheel();}

    private void OpenInput(string mode,object? target,string title,string value,int max)
    {
        _modalMode=mode;_modalTarget=target;InputModal.Width=430;InputModal.Height=double.NaN;ModalInput.Height=48;ModalInput.TextWrapping=TextWrapping.Wrap;ModalTitle.Text=title;ModalInput.Text=value;ModalInput.MaxLength=max;ModalError.Text="";InputModal.Visibility=Visibility.Visible;CenterOnDisplay(InputModal);ModalInput.Focus();ModalInput.SelectAll();
    }
    private void ModalCancel_Click(object sender,RoutedEventArgs e)=>InputModal.Visibility=Visibility.Collapsed;
    private void ModalSave_Click(object sender,RoutedEventArgs e)
    {
        var value=ModalInput.Text.Trim(); if(string.IsNullOrWhiteSpace(value)){ModalError.Text="内容不能为空";return;}
        if(_modalMode.Contains("Repo") && _store.Data.Repositories.Any(x=>x.Name.Equals(value,StringComparison.OrdinalIgnoreCase)&&x!=_modalTarget)){ModalError.Text="仓库名称不能重复";return;}
        if((_modalMode=="addItem"||_modalMode=="editItem")&&_currentRepo!.Items.Any(x=>x.Text==value&&x!=_modalTarget)){ModalError.Text="当前仓库已有相同颜文字";return;}
        switch(_modalMode){case "addRepo":_store.Data.Repositories.Add(new(){Name=value,Order=_store.Data.Repositories.Count});break;case "renameRepo":((KaomojiRepository)_modalTarget!).Name=value;break;case "addItem":_currentRepo!.Items.Add(new(){Text=value,Order=_currentRepo.Items.Count});break;case "editItem":((KaomojiItem)_modalTarget!).Text=value;break;}
        _store.SaveAll();InputModal.Visibility=Visibility.Collapsed;if(_currentRepo is not null&&RepositoryPanel.Visibility==Visibility.Visible){RepositoryTitle.Text=_currentRepo.Name;RenderItems();}else BuildWheel();
    }

    private void DeleteRepository(KaomojiRepository repo)
    {
        if(MessageBox.Show($"删除仓库“{repo.Name}”及其中 {repo.Items.Count} 个颜文字？","确认删除",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        _store.Data.Repositories.Remove(repo);_store.SaveAll();BuildWheel();
    }
    private void DeleteItem(KaomojiItem item){if(MessageBox.Show($"删除“{item.Text}”？","确认删除",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;_currentRepo!.Items.Remove(item);_store.SaveAll();RenderItems();}
    private void MoveItem(KaomojiItem item,KaomojiRepository target){_currentRepo!.Items.Remove(item);target.Items.Add(item);item.Order=target.Items.Count-1;_store.SaveAll();RenderItems();}

    private void OpenSettings(){WheelCanvas.Visibility=Visibility.Collapsed;RepositoryPanel.Visibility=Visibility.Collapsed;SettingsPanel.Visibility=Visibility.Visible;Center(SettingsPanel);ShowHotkeySettings();}
    private void HotkeyTab_Click(object sender,RoutedEventArgs e)=>ShowHotkeySettings();
    private void StartupTab_Click(object sender,RoutedEventArgs e)
    {
        SelectSettingsTab(StartupTabButton);var panel=new StackPanel();panel.Children.Add(Heading("开机自启"));panel.Children.Add(Description("登录 Windows 后让颜文字轮盘自动进入后台托盘。"));var check=new CheckBox{Content="登录后自动启动",Style=(Style)FindResource("ToggleSwitch"),IsChecked=_store.Settings.AutoStart,Margin=new Thickness(0,20,0,0)};check.Checked+=(_,_)=>SetStartup(true);check.Unchecked+=(_,_)=>SetStartup(false);panel.Children.Add(check);SetSettingsContent(panel);
    }
    private void SetStartup(bool enabled){_store.Settings.AutoStart=enabled;StartupService.SetEnabled(enabled);_store.SaveAll();}
    private void ShowHotkeySettings()
    {
        SelectSettingsTab(HotkeyTabButton);var panel=new StackPanel();panel.Children.Add(Heading("唤出快捷键"));panel.Children.Add(Description("点击下方区域，然后按下新的组合键。至少需要一个修饰键。"));var box=new TextBox{Text=_store.Settings.Hotkey,Margin=new Thickness(0,18,0,10),Height=46,MinWidth=220,IsReadOnly=true};
        box.PreviewKeyDown+=(s,e)=>{e.Handled=true;var mods=Keyboard.Modifiers;var key=e.Key==Key.System?e.SystemKey:e.Key;if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)return;var list=new List<string>();if(mods.HasFlag(ModifierKeys.Control))list.Add("Ctrl");if(mods.HasFlag(ModifierKeys.Alt))list.Add("Alt");if(mods.HasFlag(ModifierKeys.Shift))list.Add("Shift");if(list.Count==0)return;list.Add(key.ToString().ToUpperInvariant());box.Text=string.Join("+",list);}; panel.Children.Add(box);
        var status=new TextBlock{Foreground=(Brush)FindResource("MutedBrush"),Margin=new Thickness(0,6,0,0)};var apply=new Button{Content="应用",Style=(Style)FindResource("GlassButton"),HorizontalAlignment=System.Windows.HorizontalAlignment.Right};apply.Click+=(_,_)=>{if(_hotkey!.Register(box.Text)){_store.Settings.Hotkey=box.Text;_store.SaveAll();status.Text="已应用";}else{_hotkey.Register(_store.Settings.Hotkey);status.Text="组合无效或已被占用";status.Foreground=Brushes.Salmon;}};panel.Children.Add(apply);panel.Children.Add(status);SetSettingsContent(panel);
    }
    private void OrderTab_Click(object sender,RoutedEventArgs e)
    {
        SelectSettingsTab(OrderTabButton);var grid=new Grid();grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());grid.Children.Add(Heading("仓库顺序"));var hint=Description("按住任意仓库行并拖动，即可调整轮盘序号。");Grid.SetRow(hint,1);grid.Children.Add(hint);_orderList=new ListBox{Margin=new Thickness(0,14,0,0),Background=Brushes.Transparent,BorderBrush=new SolidColorBrush(Color.FromArgb(100,122,132,148)),BorderThickness=new Thickness(1),Padding=new Thickness(6),Foreground=Brushes.White,AllowDrop=true,ItemsSource=_store.Data.Repositories,ItemTemplate=(DataTemplate)FindResource("RepositoryOrderTemplate")};ScrollViewer.SetVerticalScrollBarVisibility(_orderList,ScrollBarVisibility.Auto);ScrollViewer.SetHorizontalScrollBarVisibility(_orderList,ScrollBarVisibility.Disabled);_orderList.PreviewMouseLeftButtonDown+=OrderList_MouseDown;_orderList.MouseMove+=OrderList_MouseMove;_orderList.DragOver+=OrderList_DragOver;_orderList.DragLeave+=OrderList_DragLeave;_orderList.Drop+=OrderList_Drop;var decorator=new AdornerDecorator{Child=_orderList};Grid.SetRow(decorator,2);grid.Children.Add(decorator);SetSettingsContent(grid);
    }
    private void OrderList_MouseDown(object sender,MouseButtonEventArgs e){if(_orderList is null)return;_dragStart=e.GetPosition(_orderList);var container=ItemsControl.ContainerFromElement(_orderList,e.OriginalSource as DependencyObject) as ListBoxItem;_orderList.SelectedItem=container?.DataContext;}
    private void OrderList_MouseMove(object sender,MouseEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed||_orderList?.SelectedItem is not KaomojiRepository source)return;
        if((e.GetPosition(_orderList)-_dragStart).Length<=8)return;
        _dragSourceContainer=_orderList.ItemContainerGenerator.ContainerFromItem(source) as ListBoxItem;if(_dragSourceContainer is not null)_dragSourceContainer.Opacity=.32;
        try{_orderAdornerLayer=AdornerLayer.GetAdornerLayer(_orderList);if(_orderAdornerLayer is not null){_orderDragAdorner=new OrderDragAdorner(_orderList,source.DisplayOrder,source.Name);_orderAdornerLayer.Add(_orderDragAdorner);_orderDragAdorner.Update(e.GetPosition(_orderList),null,true);}}
        catch(Exception ex){FileLogger.TryWrite(AppContext.BaseDirectory,ex);_orderDragAdorner=null;_orderAdornerLayer=null;}
        try{DragDrop.DoDragDrop(_orderList,source,DragDropEffects.Move);}
        finally{RemoveOrderDragAdorner();}
    }
    private void RemoveOrderDragAdorner(){ClearDropTarget();if(_dragSourceContainer is not null)_dragSourceContainer.Opacity=1;_dragSourceContainer=null;var layer=_orderAdornerLayer;var adorner=_orderDragAdorner;_orderDragAdorner=null;_orderAdornerLayer=null;if(layer is null||adorner is null||_orderList is null)return;try{if(layer.GetAdorners(_orderList)?.Contains(adorner)==true)layer.Remove(adorner);}catch(Exception ex){FileLogger.TryWrite(AppContext.BaseDirectory,ex);}}
    private void ClearDropTarget(){if(_dropTargetContainer is not null)_dropTargetContainer.Tag=null;_dropTargetContainer=null;_pendingInsertionIndex=null;}
    private void OrderList_DragOver(object sender,DragEventArgs e)
    {
        if(_orderList is null||!e.Data.GetDataPresent(typeof(KaomojiRepository))){e.Effects=DragDropEffects.None;return;}
        e.Effects=DragDropEffects.Move;e.Handled=true;var point=e.GetPosition(_orderList);AutoScrollOrderList(point);ShowInsertionIndicator(CalculateInsertionIndex(point));
        _orderDragAdorner?.Update(point,null,true);
    }
    private void OrderList_DragLeave(object sender,DragEventArgs e)
    {
        if(_orderList is null)return;var point=e.GetPosition(_orderList);
        if(point.X>=0&&point.X<=_orderList.ActualWidth&&point.Y>=0&&point.Y<=_orderList.ActualHeight)return;
        ClearDropTarget();_orderDragAdorner?.Update(new Point(0,0),null,false);
    }
    private int CalculateInsertionIndex(Point point)
    {
        if(_orderList is null)return 0;var count=_orderList.Items.Count;var lastRealized=-1;
        for(var i=0;i<count;i++){if(_orderList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)continue;lastRealized=i;var top=item.TranslatePoint(new Point(0,0),_orderList).Y;if(point.Y<top+item.ActualHeight/2)return i;}
        return lastRealized>=0?Math.Min(count,lastRealized+1):0;
    }
    private void ShowInsertionIndicator(int index)
    {
        if(_orderList is null)return;var insertionIndex=Math.Clamp(index,0,_orderList.Items.Count);ListBoxItem? target=null;string? state=null;
        if(insertionIndex<_orderList.Items.Count){target=_orderList.ItemContainerGenerator.ContainerFromIndex(insertionIndex) as ListBoxItem;if(target is not null)state="DropBefore";}
        if(target is null){for(var i=Math.Min(insertionIndex-1,_orderList.Items.Count-1);i>=0;i--){if(_orderList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem realized){target=realized;state="DropAfter";break;}}}
        if(_pendingInsertionIndex==insertionIndex&&ReferenceEquals(_dropTargetContainer,target)&&Equals(target?.Tag,state))return;
        if(_dropTargetContainer is not null&&!ReferenceEquals(_dropTargetContainer,target))_dropTargetContainer.Tag=null;
        _dropTargetContainer=target;_pendingInsertionIndex=insertionIndex;if(_dropTargetContainer is not null)_dropTargetContainer.Tag=state;
    }
    private void AutoScrollOrderList(Point point)
    {
        if(_orderList is null||DateTime.UtcNow-_lastDragScroll<TimeSpan.FromMilliseconds(90))return;var viewer=FindVisualChild<ScrollViewer>(_orderList);if(viewer is null)return;
        if(point.Y<28){viewer.LineUp();_lastDragScroll=DateTime.UtcNow;_orderList.UpdateLayout();}else if(point.Y>_orderList.ActualHeight-28){viewer.LineDown();_lastDragScroll=DateTime.UtcNow;_orderList.UpdateLayout();}
    }
    private static T? FindVisualChild<T>(DependencyObject parent) where T:DependencyObject{for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T match)return match;var nested=FindVisualChild<T>(child);if(nested is not null)return nested;}return null;}
    private void OrderList_Drop(object sender,DragEventArgs e)
    {
        if(_orderList is null||!e.Data.GetDataPresent(typeof(KaomojiRepository)))return;var source=(KaomojiRepository)e.Data.GetData(typeof(KaomojiRepository))!;var insertionIndex=CalculateInsertionIndex(e.GetPosition(_orderList));var list=_store.Data.Repositories;var oldIndex=list.IndexOf(source);if(oldIndex<0){ClearDropTarget();return;}list.RemoveAt(oldIndex);if(oldIndex<insertionIndex)insertionIndex--;insertionIndex=Math.Clamp(insertionIndex,0,list.Count);list.Insert(insertionIndex,source);ClearDropTarget();_store.SaveAll();_orderList.Items.Refresh();_orderList.SelectedItem=source;e.Handled=true;
    }
    private void MotionTab_Click(object sender,RoutedEventArgs e){SelectSettingsTab(MotionTabButton);var panel=new StackPanel();panel.Children.Add(Heading("减少动态效果"));panel.Children.Add(Description("缩短轮盘出现和翻页时的过渡动画。"));var c=new CheckBox{Content="减少界面动态效果",Style=(Style)FindResource("ToggleSwitch"),Margin=new Thickness(0,20,0,0),IsChecked=_store.Settings.ReduceMotion};c.Checked+=(_,_)=>SetMotion(true);c.Unchecked+=(_,_)=>SetMotion(false);panel.Children.Add(c);SetSettingsContent(panel);}
    private void SetMotion(bool value){_store.Settings.ReduceMotion=value;_store.SaveAll();}
    private void DataTab_Click(object sender,RoutedEventArgs e)=>ShowDataSettings();
    private void ShowDataSettings()
    {
        SelectSettingsTab(DataTabButton);
        var panel=new StackPanel();
        panel.Children.Add(Heading("数据处理"));
        panel.Children.Add(Description("通过 UTF-8 JSON 批量合并颜文字，或导出全部仓库作为备份。导入只会追加，不会覆盖或删除现有内容。"));
        var status=new TextBlock{Foreground=(Brush)FindResource("MutedBrush"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,0),FontSize=12,LineHeight=18,MinHeight=36};
        var import=DataActionCard("导入颜文字","选择 JSON 后先预览新增与重复数量，确认后自动备份并合并。","选择 JSON",()=>ImportKaomoji(status),true);
        import.Margin=new Thickness(0,16,0,0);panel.Children.Add(import);
        var export=DataActionCard("导出颜文字","导出全部仓库与当前顺序，不包含快捷键和开机自启设置。","导出 JSON",()=>ExportKaomoji(status),false);
        export.Margin=new Thickness(0,10,0,0);panel.Children.Add(export);panel.Children.Add(status);SetSettingsContent(panel);
    }
    private Border DataActionCard(string title,string description,string buttonText,Action action,bool accent)
    {
        var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var text=new StackPanel{VerticalAlignment=VerticalAlignment.Center};text.Children.Add(new TextBlock{Text=title,Foreground=Brushes.White,FontSize=14,FontWeight=FontWeights.SemiBold});text.Children.Add(new TextBlock{Text=description,Foreground=new SolidColorBrush(Color.FromRgb(151,160,174)),FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,14,0),LineHeight=17});grid.Children.Add(text);
        var button=new Button{Content=buttonText,Style=(Style)FindResource("GlassButton"),VerticalAlignment=VerticalAlignment.Center,MinWidth=82,Background=accent?new SolidColorBrush(Color.FromRgb(77,96,153)):new SolidColorBrush(Color.FromArgb(168,32,37,46))};button.Click+=(_,_)=>action();Grid.SetColumn(button,1);grid.Children.Add(button);
        return new Border{Background=new SolidColorBrush(Color.FromArgb(88,40,46,57)),BorderBrush=new SolidColorBrush(Color.FromArgb(112,132,143,160)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(11),Padding=new Thickness(12),Child=grid};
    }
    private void ImportKaomoji(TextBlock status)
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="导入颜文字 JSON",Filter="颜文字 JSON (*.json)|*.json|所有文件 (*.*)|*.*",CheckFileExists=true,Multiselect=false};
        var templates=IOPath.Combine(AppContext.BaseDirectory,"ImportTemplates");if(IODirectory.Exists(templates))dialog.InitialDirectory=templates;
        if(dialog.ShowDialog(this)!=true)return;
        try
        {
            var document=KaomojiTransferService.Load(dialog.FileName);var preview=KaomojiTransferService.Analyze(_store.Data,document);
            if(preview.AddedRepositories==0&&preview.AddedItems==0){status.Foreground=(Brush)FindResource("MutedBrush");status.Text=$"“{IOPath.GetFileName(dialog.FileName)}”没有可新增内容，已跳过 {preview.SkippedDuplicates} 条重复颜文字。";return;}
            var message=$"文件：{IOPath.GetFileName(dialog.FileName)}\n包含：{preview.SourceRepositories} 个仓库，{preview.SourceItems} 条颜文字\n\n将新增：{preview.AddedRepositories} 个仓库，{preview.AddedItems} 条颜文字\n将跳过：{preview.SkippedDuplicates} 条重复颜文字\n\n导入前会自动备份现有数据，是否继续？";
            if(MessageBox.Show(message,"确认导入",MessageBoxButton.YesNo,MessageBoxImage.Information)!=MessageBoxResult.Yes)return;
            var result=_store.Import(document);status.Foreground=new SolidColorBrush(Color.FromRgb(167,217,197));status.Text=$"导入完成：新增 {result.AddedRepositories} 个仓库、{result.AddedItems} 条颜文字，跳过 {result.SkippedDuplicates} 条重复内容。";
        }
        catch(Exception ex){FileLogger.TryWrite(AppContext.BaseDirectory,ex);status.Foreground=Brushes.Salmon;status.Text=$"导入失败：{ex.Message}";}
    }
    private void ExportKaomoji(TextBlock status)
    {
        var dialog=new Microsoft.Win32.SaveFileDialog{Title="导出颜文字 JSON",Filter="颜文字 JSON (*.json)|*.json",DefaultExt=".json",AddExtension=true,OverwritePrompt=true,FileName=$"KaomojiWheel-{DateTime.Now:yyyyMMdd-HHmmss}.json",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)};
        if(dialog.ShowDialog(this)!=true)return;
        try{KaomojiTransferService.Export(dialog.FileName,_store.Data);var itemCount=_store.Data.Repositories.Sum(x=>x.Items.Count);status.Foreground=new SolidColorBrush(Color.FromRgb(167,217,197));status.Text=$"已导出 {_store.Data.Repositories.Count} 个仓库、{itemCount} 条颜文字：{dialog.FileName}";}
        catch(Exception ex){FileLogger.TryWrite(AppContext.BaseDirectory,ex);status.Foreground=Brushes.Salmon;status.Text=$"导出失败：{ex.Message}";}
    }
    private void UpdateTab_Click(object sender,RoutedEventArgs e)=>ShowUpdateSettings();
    private void ShowUpdateSettings()
    {
        SelectSettingsTab(UpdateTabButton);var panel=new StackPanel();panel.Children.Add(Heading("软件更新"));panel.Children.Add(Description($"当前版本  v{UpdateService.CurrentVersion}"));
        var automatic=new CheckBox{Content="自动检查更新（最多每 24 小时一次）",Style=(Style)FindResource("ToggleSwitch"),Margin=new Thickness(0,14,0,0),IsChecked=_store.Settings.AutoCheckUpdates};
        automatic.Checked+=async (_,_)=>{_store.Settings.AutoCheckUpdates=true;_store.SaveAll();_updateTimer.Start();await CheckForUpdatesIfDueAsync();};automatic.Unchecked+=(_,_)=>{_store.Settings.AutoCheckUpdates=false;_store.SaveAll();_updateTimer.Stop();};panel.Children.Add(automatic);
        _updateStatus=new TextBlock{Foreground=(Brush)FindResource("MutedBrush"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,0),FontSize=12,LineHeight=18};panel.Children.Add(_updateStatus);
        _updateNotes=new TextBlock{Foreground=new SolidColorBrush(Color.FromRgb(220,224,232)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,0),FontSize=12,MaxHeight=42,TextTrimming=TextTrimming.CharacterEllipsis};panel.Children.Add(_updateNotes);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        _checkUpdateButton=new Button{Content="检查更新",Style=(Style)FindResource("GlassButton"),Margin=new Thickness(0,0,8,0)};_checkUpdateButton.Click+=async (_,_)=>await CheckForUpdatesAsync(true);buttons.Children.Add(_checkUpdateButton);
        _installUpdateButton=new Button{Content="下载并安装",Style=(Style)FindResource("GlassButton"),Background=new SolidColorBrush(Color.FromRgb(83,105,183))};_installUpdateButton.Click+=InstallUpdate_Click;buttons.Children.Add(_installUpdateButton);panel.Children.Add(buttons);SetSettingsContent(panel);RefreshUpdateStatus();
    }
    private async Task CheckForUpdatesIfDueAsync()
    {
        if(!_store.Settings.AutoCheckUpdates||_checkingUpdate)return;var last=_store.Settings.LastUpdateCheckUtc;if(last.HasValue&&DateTimeOffset.UtcNow-last.Value<TimeSpan.FromHours(24))return;await CheckForUpdatesAsync(false);
    }
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if(_checkingUpdate)return;_checkingUpdate=true;if(_checkUpdateButton is not null)_checkUpdateButton.IsEnabled=false;if(_updateStatus is not null)_updateStatus.Text="正在连接 GitHub 检查更新…";
        try
        {
            _availableUpdate=await _updateService.CheckAsync();_store.Settings.LastUpdateCheckUtc=DateTimeOffset.UtcNow;_store.SaveAll();RefreshUpdateStatus();
            if(_availableUpdate is not null&&_notifiedUpdateVersion!=_availableUpdate.Version){_notifiedUpdateVersion=_availableUpdate.Version;UpdateAvailable?.Invoke(_availableUpdate);}
        }
        catch(Exception ex){_store.Settings.LastUpdateCheckUtc=DateTimeOffset.UtcNow;_store.SaveAll();if(_updateStatus is not null)_updateStatus.Text=manual?$"检查失败：{ex.Message}":"自动检查暂时失败，可稍后手动重试。";}
        finally{_checkingUpdate=false;if(_checkUpdateButton is not null)_checkUpdateButton.IsEnabled=true;}
    }
    private void RefreshUpdateStatus()
    {
        if(_updateStatus is null||_updateNotes is null||_installUpdateButton is null)return;
        if(_availableUpdate is null){_updateStatus.Text="当前已是最新版本。";_updateNotes.Text="";_installUpdateButton.Visibility=Visibility.Collapsed;return;}
        _updateStatus.Text=$"发现新版本 v{_availableUpdate.Version}";_updateNotes.Text=_availableUpdate.ReleaseNotes;_installUpdateButton.Visibility=Visibility.Visible;
    }
    private async void InstallUpdate_Click(object sender,RoutedEventArgs e)
    {
        if(_availableUpdate is null||_installUpdateButton is null||_updateStatus is null)return;
        var message=$"将更新到 v{_availableUpdate.Version}。\n\n程序会下载并校验更新包，备份当前程序文件，保留 Data 数据，然后自动重启。是否继续？";
        if(MessageBox.Show(message,"安装更新",MessageBoxButton.YesNo,MessageBoxImage.Information)!=MessageBoxResult.Yes)return;
        _installUpdateButton.IsEnabled=false;if(_checkUpdateButton is not null)_checkUpdateButton.IsEnabled=false;
        try
        {
            var progress=new Progress<int>(value=>_updateStatus.Text=$"正在下载更新… {value}%");await _updateService.LaunchInstallerAsync(_availableUpdate,progress);_updateStatus.Text="校验完成，正在退出并安装…";Application.Current.Shutdown();
        }
        catch(Exception ex){_updateStatus.Text=$"安装准备失败：{ex.Message}";_installUpdateButton.IsEnabled=true;if(_checkUpdateButton is not null)_checkUpdateButton.IsEnabled=true;}
    }
    private static TextBlock Heading(string text)=>new(){Text=text,Foreground=Brushes.White,FontSize=20,FontWeight=FontWeights.SemiBold};
    private static TextBlock Description(string text)=>new(){Text=text,Foreground=new SolidColorBrush(Color.FromRgb(151,160,174)),FontSize=12,FontWeight=FontWeights.Medium,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,7,0,0),LineHeight=19};
    private void SelectSettingsTab(Button selected){foreach(var button in new[]{HotkeyTabButton,StartupTabButton,OrderTabButton,MotionTabButton,DataTabButton,UpdateTabButton})button.Tag=button==selected?"Selected":null;}
    private void SetSettingsContent(UIElement content){SettingsContent.Children.Clear();SettingsContent.Children.Add(content);}

    private void OnboardingDone_Click(object sender,RoutedEventArgs e){_store.Settings.OnboardingSeen=true;_store.SaveAll();HideOverlay();}
    private void Window_MouseLeftButtonDown(object sender,MouseButtonEventArgs e){if(InputModal.Visibility==Visibility.Visible||OnboardingPanel.Visibility==Visibility.Visible)return;if(e.OriginalSource==Root)HideOverlay();}
    private void Modal_MouseLeftButtonDown(object sender,MouseButtonEventArgs e)=>e.Handled=true;
    private void HideOverlay(){Opacity=0;Root.Opacity=0;Root.Visibility=Visibility.Hidden;WheelCanvas.Children.Clear();Toast.Visibility=Visibility.Collapsed;_toastTimer.Stop();Dispatcher.Invoke(DispatcherPriority.Render,new Action(()=>{}));try{DwmFlush();}catch(DllNotFoundException){}Hide();if(_previousWindow!=IntPtr.Zero)SetForegroundWindow(_previousWindow);}
    private void ShowToast(string text,bool error=false){ToastText.Text=text;ToastText.Foreground=error?Brushes.Salmon:Brushes.White;Toast.Visibility=Visibility.Visible;_toastTimer.Stop();_toastTimer.Start();}
    private void Center(FrameworkElement e){e.Measure(new Size(Math.Max(0,Width-Safety*2),Math.Max(0,Height-Safety*2)));var w=double.IsNaN(e.Width)?e.DesiredSize.Width:e.Width;var h=double.IsNaN(e.Height)?e.DesiredSize.Height:e.Height;e.HorizontalAlignment=System.Windows.HorizontalAlignment.Left;e.VerticalAlignment=System.Windows.VerticalAlignment.Top;e.Margin=new Thickness(Math.Max(Safety,Math.Min(Width-w-Safety,_center.X-w/2)),Math.Max(Safety,Math.Min(Height-h-Safety,_center.Y-h/2)),0,0);}
    private void CenterOnDisplay(FrameworkElement e){e.HorizontalAlignment=System.Windows.HorizontalAlignment.Left;e.VerticalAlignment=System.Windows.VerticalAlignment.Top;e.Margin=new Thickness(0);e.InvalidateMeasure();e.Measure(new Size(Math.Max(0,Width-Safety*2),Math.Max(0,Height-Safety*2)));var w=double.IsNaN(e.Width)?e.DesiredSize.Width:e.Width;var h=double.IsNaN(e.Height)?e.DesiredSize.Height:e.Height;e.Margin=new Thickness(Math.Max(Safety,Math.Min(Width-w-Safety,_displayCenter.X-w/2)),Math.Max(Safety,Math.Min(Height-h-Safety,_displayCenter.Y-h/2)),0,0);}
    private static void Place(FrameworkElement e,Point center,double w,double h){Canvas.SetLeft(e,center.X-w/2);Canvas.SetTop(e,center.Y-h/2);}
    public void Dispose(){_hotkey?.Dispose();Close();}
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("dwmapi.dll")]private static extern int DwmFlush();

    private sealed class OrderDragAdorner : Adorner
    {
        private readonly VisualCollection _visuals;private readonly Border _ghost;private readonly Canvas _indicator;private Point _pointer;private double? _lineY;private bool _showGhost;
        public OrderDragAdorner(UIElement adorned,int order,string name):base(adorned)
        {
            IsHitTestVisible=false;_visuals=new VisualCollection(this);var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(34)});row.ColumnDefinitions.Add(new ColumnDefinition());
            var number=new Border{Width=24,Height=24,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Background=new SolidColorBrush(Color.FromArgb(100,52,59,71)),BorderBrush=new SolidColorBrush(Color.FromArgb(164,142,154,170)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(7),Child=new TextBlock{Text=order.ToString(),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Foreground=new SolidColorBrush(Color.FromRgb(240,242,246)),FontSize=12,FontWeight=FontWeights.SemiBold}};row.Children.Add(number);
            var label=new TextBlock{Text=name,Foreground=Brushes.White,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(label,1);row.Children.Add(label);
            _ghost=new Border{Height=38,Background=new SolidColorBrush(Color.FromArgb(218,42,48,59)),BorderBrush=new SolidColorBrush(Color.FromArgb(230,178,188,204)),BorderThickness=new Thickness(1.4),CornerRadius=new CornerRadius(10),Padding=new Thickness(10,0,10,0),Child=row,Opacity=.66};_visuals.Add(_ghost);
            _indicator=new Canvas{Height=8,IsHitTestVisible=false};var accent=new SolidColorBrush(Color.FromRgb(105,190,255));var line=new Border{Height=3.5,Background=accent,CornerRadius=new CornerRadius(2)};Canvas.SetTop(line,2.25);_indicator.Children.Add(line);var leftDot=new Ellipse{Width=8,Height=8,Fill=accent};var rightDot=new Ellipse{Width=8,Height=8,Fill=accent};_indicator.Children.Add(leftDot);_indicator.Children.Add(rightDot);_visuals.Add(_indicator);
        }
        public void Update(Point pointer,double? lineY,bool showGhost){_pointer=pointer;_lineY=lineY;_showGhost=showGhost;_ghost.Visibility=showGhost?Visibility.Visible:Visibility.Hidden;_indicator.Visibility=lineY.HasValue?Visibility.Visible:Visibility.Hidden;InvalidateArrange();InvalidateVisual();}
        protected override int VisualChildrenCount=>_visuals.Count;protected override Visual GetVisualChild(int index)=>_visuals[index];
        protected override Size MeasureOverride(Size constraint){var width=Math.Max(120,AdornedElement.RenderSize.Width-16);_ghost.Measure(new Size(width,38));_indicator.Measure(new Size(width,8));return AdornedElement.RenderSize;}
        protected override Size ArrangeOverride(Size finalSize){var width=Math.Max(120,finalSize.Width-16);var y=Math.Clamp(_pointer.Y-19,4,Math.Max(4,finalSize.Height-42));_ghost.Arrange(new Rect(8,y,width,38));if(_lineY is double lineY){var indicatorWidth=Math.Max(16,finalSize.Width-16);var visibleY=Math.Clamp(lineY,4,Math.Max(4,finalSize.Height-4));if(_indicator.Children[0] is Border line)line.Width=Math.Max(4,indicatorWidth-8);Canvas.SetLeft(_indicator.Children[0],4);Canvas.SetLeft(_indicator.Children[1],0);Canvas.SetLeft(_indicator.Children[2],Math.Max(0,indicatorWidth-8));_indicator.Arrange(new Rect(8,visibleY-4,indicatorWidth,8));}else _indicator.Arrange(new Rect(0,0,0,0));return finalSize;}
    }
}
