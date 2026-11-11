using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenOffTray.Playback;
using ScreenOffTray.SettingsTransport;

namespace ScreenOffTray.SettingsUi
{
    public sealed partial class SettingsWindow : Window
    {
        private sealed class WhitelistItem
        {
            internal string Path;
            internal string Display;
            public override string ToString(){return Display;}
        }
        private sealed class SourceRow
        {
            internal Border Root;
            internal Image Icon;
            internal TextBlock Name;
            internal Button Add;
        }

        private readonly bool english;
        private readonly ObservableCollection<WhitelistItem> whitelistItems=new ObservableCollection<WhitelistItem>();
        private readonly Dictionary<string,SourceRow> sourceRows=new Dictionary<string,SourceRow>(StringComparer.OrdinalIgnoreCase);
        private readonly ApplicationIcons icons;
        private bool settingValues,closed,migrationNeeded,listBusy,viewBusy,playbackLimitReached;
        private string migrationKey,listKey,sourcesKey,backgroundMode="geometry";
        private PlayingSource[] currentSources=new PlayingSource[0];
        private string[] appliedWhitelist=new string[0];

        internal event Action<int> MigrationRequested;
        internal event Action DisplaySettingsRequested;
        public event Action<bool> CaptureChanged;
        public event Action SaveRequested,WhitelistAddRequested,WhitelistRemoveRequested,RecheckRequested,RefreshRequested,UpdateRequested;
        public event Action PrepareRequested,CancelPrepareRequested,LanguageRequested;
        internal event Action<string> AddPathRequested;
        internal bool ShortcutEdited {get;set;}

        public bool IsCaptureFocused {get{return HotkeyInput.IsKeyboardFocusWithin && IsActive;}}
        public int Minutes {get{int value;return Int32.TryParse(MinutesInput.Text,out value)?Math.Max(1,Math.Min(1440,value)):3;}}
        public int ProtectionMinutes {get{int value;return Int32.TryParse(ProtectionMinutesInput.Text,out value)?Math.Max(1,Math.Min(1440,value)):60;}}
        public string Shortcut {get{return HotkeyInput.Text;}}
        public bool Startup {get{return StartupSwitch.IsChecked==true;}}
        public bool Updates {get{return UpdatesSwitch.IsChecked==true;}}
        public bool Cap {get{return ProtectionLimitSwitch.IsChecked==true;}}
        public string UiLanguage {get{return EnglishChoice.IsChecked==true?"en":"zh-CN";}}
        public string InterfaceTheme {get{return backgroundMode;}}
        public string SelectedPath {get{var item=WhitelistCombo.SelectedItem as WhitelistItem;return item==null?null:item.Path;}}

        public static Task PreloadArtwork(){return Task.FromResult(0);}

        public SettingsWindow(bool english,Size? workArea=null)
        {
            this.english=english;
            InitializeComponent();
            if(english)LocalizeStaticTexts(this);
            Size area=workArea??new Size(SystemParameters.WorkArea.Width,SystemParameters.WorkArea.Height);
            double availableWidth=Math.Max(1,area.Width-32);
            double availableHeight=Math.Max(1,area.Height-32);
            double width=Math.Min(1120,availableWidth);
            double height=width*5.0/7.0;
            if(height>availableHeight){height=availableHeight;width=height*7.0/5.0;}
            Width=width;Height=height;
            Title=T("ScreenOffTray · 设置","ScreenOffTray · Settings");
            WhitelistCombo.ItemsSource=whitelistItems;
            icons=new ApplicationIcons(Dispatcher);icons.Changed+=UpdateIcons;
            Closed+=delegate{closed=true;icons.Changed-=UpdateIcons;icons.Dispose();};
            HotkeyInput.GotKeyboardFocus+=delegate{NotifyCapture();};
            HotkeyInput.LostKeyboardFocus+=delegate{if(CaptureChanged!=null)CaptureChanged(false);};
            Activated+=delegate{NotifyCapture();};
            Deactivated+=delegate{if(CaptureChanged!=null)CaptureChanged(false);};
            ChineseChoice.Click+=OnLanguageChoice;
            EnglishChoice.Click+=OnLanguageChoice;
            ProtectionLimitSwitch.Checked+=delegate{ProtectionMinutesInput.IsEnabled=true;};
            ProtectionLimitSwitch.Unchecked+=delegate{ProtectionMinutesInput.IsEnabled=false;};
            ApplyBackground(backgroundMode);
            SetSources(new PlayingSource[0],true);
            ApplyBusy();
        }

        private string T(string zh,string en){return english?en:zh;}

        private void LocalizeStaticTexts(DependencyObject root)
        {
            var text=root as TextBlock;
            if(text!=null)
            {
                string value;
                if(StaticTranslations.TryGetValue(text.Text,out value))text.Text=value;
                else if(text.Text=="版本 0.2.0 · Windows x64")text.Text="Version 0.2.0 · Windows x64";
            }
            var control=root as ContentControl;
            if(control!=null && control.Content is string)
            {
                string value;
                if(StaticTranslations.TryGetValue((string)control.Content,out value))control.Content=value;
            }
            var framework=root as FrameworkElement;
            if(framework!=null && framework.ToolTip is string)
            {
                string value;
                if(StaticTranslations.TryGetValue((string)framework.ToolTip,out value))framework.ToolTip=value;
            }
            foreach(object child in LogicalTreeHelper.GetChildren(root))
                if(child is DependencyObject)LocalizeStaticTexts((DependencyObject)child);
        }

        private static readonly Dictionary<string,string> StaticTranslations=new Dictionary<string,string>{
            {"通用","General"},{"播放保护","Playback protection"},{"关于","About"},
            {"启动方式、显示准备与关屏设置","Startup, display preparation and display-off settings"},
            {"显示准备","Display preparation"},{"一键准备","Prepare"},{"重新检查","Check again"},
            {"每次重新识别实体屏与虚拟屏，并按需匹配缩放","Recheck physical and virtual displays and match scaling when needed"},
            {"已就绪","Ready"},{"检查中…","Checking…"},{"需要准备","Needs preparation"},{"未启用","Disabled"},{"检查失败","Check failed"},
            {"检查","Check"},
            {"基础设置","Basic settings"},{"空闲关屏","Idle display off"},{"没有实体键鼠操作后自动关闭显示器","Turn off the display after no physical input"},
            {"关屏快捷键","Display-off hotkey"},{"点击输入框后按组合键；第一次实体输入只负责亮屏","Focus the box and press a shortcut. The first physical input only wakes the display."},
            {"恢复默认","Use default"},{"界面语言","Language"},{"切换后将使用所选语言","Use the selected language"},{"简体中文","Simplified Chinese"},
            {"启动与更新","Startup and updates"},{"开机启动","Start with Windows"},{"登录 Windows 后自动在托盘运行","Run in the notification area after Windows sign-in"},
            {"自动检查更新","Automatically check for updates"},{"发现新版本时提示，不会自动安装","Notify when an update is available; never install automatically"},
            {"发现不同的旧关闭时间","Different previous timeouts found"},{"请选择要保留的值，程序不会自行覆盖。","Choose the value to keep. Existing settings are not overwritten automatically."},
            {"应用","Apply"},{"取消准备","Cancel preparation"},{"显示设置","Display settings"},{"诊断详情","Diagnostic details"},
            {"只关闭显示器，后台程序和任务继续运行。","Turns off the display while apps and background tasks keep running."},
            {"正在播放","Currently playing"},{"当前播放程序","Current playback"},{"刷新","Refresh"},{"暂无正在播放的程序","No apps playing"},
            {"白名单程序开始播放后会自动出现在这里","Allowlisted apps appear here when they start playing"},
            {"白名单","Allowlist"},{"移除","Remove"},{"即时保存","Saved instantly"},
            {"白名单按程序完整路径保存","Allowlisted apps are stored by their full executable path"},
            {"名单内程序播放音频时，暂缓自动关屏。添加和移除即时保存。","Audio played by listed apps pauses automatic display off. Add and remove changes are saved immediately."},
            {"保护上限","Protection limit"},{"连续播放时也关闭屏幕","Turn off the display during continuous playback"},
            {"无实体操作达到上限后，播放不再阻止关屏","After the physical-idle limit, playback no longer prevents display off"},{"分钟","minutes"},
            {"关屏与唤醒","Display off and wake"},{"立即关屏","Turn off now"},{"默认快捷键，可在通用页修改","Default hotkey; change it on General"},
            {"3 秒防误触","3-second guard"},{"保护期内的实体输入不会唤醒，也不会传给当前软件","Physical input during the guard neither wakes nor reaches the active app"},
            {"首次输入只负责亮屏","First input only wakes"},{"松开后再次按键或点击，才会正常操作软件","Release, then press or click again to control the app"},
            {"外观","Appearance"},{"窗口外观","Window appearance"},{"三套外观只改变设置窗口材质，选择会随设置保存","The three appearances change only this Settings window and are saved with your settings"},
            {"几何","Geometry"},{"暖白","Warm white"},{"黑色","Black"},
            {"数据与帮助","Data and help"},{"设置保存在本机","Settings stay on this PC"},{"安装版由独立后台读写；当前预览不会修改正式配置","The installed app stores settings through its local backend"},
            {"本地","Local"},{"打开使用帮助","Open help"},{"查看显示准备、播放保护与快捷键说明","Read about display preparation, playback protection and hotkeys"},{"打开","Open"},
            {"检查更新","Check for updates"},{"更改将在保存后生效","Changes take effect after saving"},{"白名单增删即时保存","Allowlist changes save immediately"},
            {"预览模式 · 不读取正式配置","Preview mode · formal configuration is not read"},{"取消","Cancel"},{"保存","Save"},
            {"关闭设置 (Esc)","Close Settings (Esc)"},{"聚焦后直接按新的实体快捷键","Focus the box, then press a new physical shortcut"},
            {"重新检查显示条件","Recheck display conditions"},{"显示已就绪","Display ready"},
            {"检测到 1 个白名单程序正在播放，自动关屏已暂停","One allowlisted app is playing; automatic display off is paused"}
        };

        public void ObserveKey(int key,uint modifiers,bool up)
        {
            if(!IsCaptureFocused || up)return;
            HotkeyBinding binding;string why;
            if(HotkeyBinding.TryCreate(modifiers,key,out binding,out why)){HotkeyInput.Text=binding.DisplayText;ShortcutEdited=true;SetError("");}
            else SetError(why);
        }

        public void SetBusy(bool busy){viewBusy=busy;ApplyBusy();}
        public void SetDisconnected(){SetBusy(true);CancelButton.IsEnabled=true;}
        public void SetListBusy(bool busy){listBusy=busy;ApplyBusy();}

        internal void SetMigrationChoices(int[] values)
        {
            values=values??new int[0];string key=String.Join(",",values);if(migrationKey==key)return;migrationKey=key;
            migrationNeeded=values.Length>0;MigrationChoices.ItemsSource=values;MigrationChoices.SelectedIndex=-1;
            MigrationPanel.Visibility=migrationNeeded?Visibility.Visible:Visibility.Collapsed;ApplyBusy();
        }

        public void ShowSetup(bool ready,bool checking,bool preparing,bool canPrepare,string detail,string diagnostics)
        {
            SetupNotice.Visibility=ready && !preparing?Visibility.Collapsed:Visibility.Visible;
            NoticeTitle.Text=T("显示准备","Display preparation");
            NoticeBody.Text=migrationNeeded?T("发现不同的旧关闭时间，请选择要保留的值。","Previous timeouts differ. Choose the value to keep."):(detail??"");
            DiagnosticText.Text=diagnostics??"";
            PrepareButton.IsEnabled=canPrepare && !checking && !preparing;
            NoticeAction.IsEnabled=canPrepare && !checking && !preparing;
            NoticeAction.Content=T("一键准备","Prepare");
            CancelPrepareButton.Visibility=preparing?Visibility.Visible:Visibility.Collapsed;
            if(ready){NoticeIconSurface.Background=(Brush)FindResource("Success");SetupNotice.Background=(Brush)FindResource("SuccessSurface");NoticeIcon.Text="✓";}
            else if(checking||preparing){NoticeIconSurface.Background=(Brush)FindResource("Warning");SetupNotice.Background=(Brush)FindResource("WarningSurface");NoticeIcon.Text="…";}
            else {NoticeIconSurface.Background=(Brush)FindResource("Warning");SetupNotice.Background=(Brush)FindResource("WarningSurface");NoticeIcon.Text="!";}
        }

        public void SetList(string[] paths)
        {
            paths=paths??new string[0];string key=String.Join("\n",paths);if(listKey==key)return;listKey=key;
            string selected=SelectedPath;appliedWhitelist=(string[])paths.Clone();whitelistItems.Clear();
            foreach(string path in paths)
            {
                string display=AudioSourceNames.Fallback(path);
                if(english)display=display.Replace("（未提供软件名）"," (no product name)");
                whitelistItems.Add(new WhitelistItem{Path=path,Display=display});
            }
            if(!String.IsNullOrEmpty(selected))
                WhitelistCombo.SelectedItem=whitelistItems.FirstOrDefault(item=>String.Equals(item.Path,selected,StringComparison.OrdinalIgnoreCase));
            if(WhitelistCombo.SelectedIndex<0 && whitelistItems.Count>0)WhitelistCombo.SelectedIndex=0;
            WhitelistLabel.Text=T("白名单（","Allowlist (")+whitelistItems.Count+T("）",")");
            ApplyBusy();UpdateSourceButtons();
        }

        internal void SetSources(PlayingSource[] sources,bool known)
        {
            sources=sources??new PlayingSource[0];
            string next=(known?"known":"unknown")+"\n"+String.Join("\n",sources.Select(source=>source.Path+"\0"+source.Name+"\0"+(source.Protected?"1":"0")));
            if(sourcesKey==next)return;sourcesKey=next;currentSources=sources;
            var identities=new HashSet<string>(sources.Select(source=>source.Path),StringComparer.OrdinalIgnoreCase);
            foreach(string path in sourceRows.Keys.Where(path=>!identities.Contains(path)).ToArray())
            {PlayingRows.Children.Remove(sourceRows[path].Root);sourceRows.Remove(path);}
            PlayingRows.Children.Clear();
            foreach(PlayingSource source in sources)
            {
                var image=new Image{Width=32,Height=32,Margin=new Thickness(0,0,12,0),Stretch=Stretch.Uniform};
                var name=new TextBlock{Text=source.Name,FontSize=14,Foreground=(Brush)FindResource("Text1"),TextTrimming=TextTrimming.CharacterEllipsis};
                var status=new TextBlock{Text=playbackLimitReached
                    ?T("正在播放 · 播放保护上限已到，不再暂停关屏","Playing · protection limit reached; auto off is no longer paused")
                    :source.Protected?T("正在播放 · 自动关屏已暂停","Playing · automatic display off is paused")
                    :T("正在播放 · 尚未加入白名单，不暂停关屏","Playing · not protected; auto off is not paused"),
                    FontSize=12,Foreground=(Brush)FindResource("Text2"),Margin=new Thickness(0,3,0,0)};
                var text=new StackPanel{VerticalAlignment=VerticalAlignment.Center};text.Children.Add(name);text.Children.Add(status);
                var add=new Button{Content=T("加入","Add"),Style=(Style)FindResource("SecondaryButton"),VerticalAlignment=VerticalAlignment.Center};
                string identity=source.Path;add.Click+=delegate{if(AddPathRequested!=null)AddPathRequested(identity);else if(WhitelistAddRequested!=null)WhitelistAddRequested();};
                var columns=new Grid();
                columns.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(44)});
                columns.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
                columns.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
                columns.Children.Add(image);Grid.SetColumn(text,1);columns.Children.Add(text);Grid.SetColumn(add,2);columns.Children.Add(add);
                var root=new Border{MinHeight=62,Padding=new Thickness(14,10,14,10),BorderBrush=(Brush)FindResource("Stroke"),BorderThickness=new Thickness(0,1,0,0),Child=columns};
                PlayingRows.Children.Add(root);sourceRows[identity]=new SourceRow{Root=root,Icon=image,Name=name,Add=add};
            }
            PlaybackEmptyPanel.Visibility=sources.Length==0?Visibility.Visible:Visibility.Collapsed;
            PlaybackSummary.Text=known
                ?(sources.Length==0?T("当前没有程序正在播放音频","No app is currently playing audio")
                    :playbackLimitReached?T("播放保护上限已到，不再暂停自动关屏","Protection limit reached; automatic display off is no longer paused")
                    :sources.Any(source=>source.Protected)?T("检测到白名单程序正在播放，自动关屏已暂停","An allowlisted app is playing; automatic display off is paused")
                    :T("检测到程序正在播放音频，加入白名单后才暂停自动关屏","Audio is playing; add an app to pause automatic display off"))
                :T("播放状态暂不可用，自动关屏将保持暂停","Playback state is unavailable; automatic display off remains paused");
            UpdateIcons();UpdateSourceButtons();
        }

        public void SetPlaybackState(bool limitReached)
        {
            if(playbackLimitReached==limitReached)return;
            playbackLimitReached=limitReached;
            sourcesKey=null;
            SetSources(currentSources,true);
        }

        public void SetStatus(string ready,string source,string caption,string protection)
        {
            string status=ready??"";
            SidebarStatusText.Text=status.Replace("✓ ","");
            PrepareStateText.Text=status.Replace("✓ ","");
            bool success=status.IndexOf("已就绪",StringComparison.Ordinal)>=0 || status.IndexOf("ready",StringComparison.OrdinalIgnoreCase)>=0;
            bool warning=status.IndexOf("检查",StringComparison.Ordinal)>=0 || status.IndexOf("check",StringComparison.OrdinalIgnoreCase)>=0;
            SidebarStatusDot.Fill=(Brush)FindResource(success?"Success":warning?"Warning":"Text3");
            PrepareStateText.Foreground=(Brush)FindResource(success?"Success":warning?"Warning":"Text2");
        }

        public void SetUpdate(string value)
        {
            UpdateStatusText.Text=value??"";
            UpdateStatusText.Visibility=String.IsNullOrEmpty(value)?Visibility.Collapsed:Visibility.Visible;
        }

        public void SetError(string value)
        {
            ErrorText.Text=value??"";
            ErrorBanner.Visibility=String.IsNullOrEmpty(value)?Visibility.Collapsed:Visibility.Visible;
        }

        public void SetValues(int minutes,string key,string lang,bool start,bool automatic,bool capped,int capMinutes,string theme)
        {
            settingValues=true;
            try
            {
                MinutesInput.Text=minutes.ToString();HotkeyInput.Text=key??"Ctrl + Alt + F10";
                ChineseChoice.IsChecked=lang!="en";EnglishChoice.IsChecked=lang=="en";
                StartupSwitch.IsChecked=start;UpdatesSwitch.IsChecked=automatic;
                ProtectionLimitSwitch.IsChecked=capped;ProtectionMinutesInput.Text=capMinutes.ToString();ProtectionMinutesInput.IsEnabled=capped;
                ApplyBackground(String.IsNullOrEmpty(theme)?"geometry":theme);
            }
            finally{settingValues=false;}
        }

        public void SetValues(int minutes,string key,string lang,bool start,bool automatic,bool capped,int capMinutes)
        {
            SetValues(minutes,key,lang,start,automatic,capped,capMinutes,"geometry");
        }

        private void ApplyBusy()
        {
            SaveButton.IsEnabled=!viewBusy && !migrationNeeded;
            CancelButton.IsEnabled=!viewBusy;
            GeneralPage.IsEnabled=!viewBusy && !migrationNeeded;
            PlaybackPage.IsEnabled=!viewBusy;
            AboutPage.IsEnabled=!viewBusy;
            WhitelistCombo.IsEnabled=!viewBusy && !listBusy && whitelistItems.Count>0;
            RemoveWhitelistButton.IsEnabled=!viewBusy && !listBusy && WhitelistCombo.SelectedItem!=null;
            UpdateSourceButtons();
        }

        private void UpdateSourceButtons()
        {
            foreach(var pair in sourceRows)
            {
                bool included=appliedWhitelist.Contains(pair.Key,StringComparer.OrdinalIgnoreCase);
                pair.Value.Add.Content=included?T("已加入","Added"):T("加入","Add");
                pair.Value.Add.IsEnabled=!included && !viewBusy && !listBusy;
            }
        }

        private void UpdateIcons()
        {
            if(closed)return;
            foreach(var pair in sourceRows)pair.Value.Icon.Source=icons.Read(pair.Key);
        }

        private void NotifyCapture(){if(CaptureChanged!=null)CaptureChanged(IsCaptureFocused);}

        private void OnLoaded(object sender,RoutedEventArgs e)
        {
            ApplyBackground(backgroundMode);
        }

        private void OnWindowKeyDown(object sender,KeyEventArgs e)
        {
            if(e.Key==Key.Escape){e.Handled=true;Close();return;}
            if(e.Key==Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){OnSaveClick(sender,e);e.Handled=true;return;}
            if(Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key>=Key.D1 && e.Key<=Key.D3)
            {
                string[] pages={"general","playback","about"};ShowPage(pages[e.Key-Key.D1]);e.Handled=true;
            }
        }

        private void OnHeaderMouseDown(object sender,MouseButtonEventArgs e)
        {
            if(e.ButtonState==MouseButtonState.Pressed && FindAncestor<Button>(e.OriginalSource as DependencyObject)==null)DragMove();
        }

        private void OnNavClick(object sender,RoutedEventArgs e)
        {
            var button=sender as RadioButton;if(button!=null)ShowPage(button.Tag as string);
        }

        private void ShowPage(string page)
        {
            page=(page??"general").ToLowerInvariant();
            GeneralPage.Visibility=page=="general"?Visibility.Visible:Visibility.Collapsed;
            PlaybackPage.Visibility=page=="playback"?Visibility.Visible:Visibility.Collapsed;
            AboutPage.Visibility=page=="about"?Visibility.Visible:Visibility.Collapsed;
            GeneralNav.IsChecked=page=="general";PlaybackNav.IsChecked=page=="playback";AboutNav.IsChecked=page=="about";
            if(page=="playback"){PageTitle.Text=T("播放保护","Playback protection");PageDescription.Text=T("正在播放、白名单与连续播放上限","Current playback, allowlist and continuous-playback limit");FooterHint.Text=T("白名单增删即时保存","Allowlist changes save immediately");}
            else if(page=="about"){PageTitle.Text=T("关于","About");PageDescription.Text=T("版本、更新、关屏规则与本地数据","Version, updates, display-off rules and local data");FooterHint.Text=T("本地设置 · 按保存生效","Local settings · saved on confirmation");}
            else {PageTitle.Text=T("通用","General");PageDescription.Text=T("启动方式、显示准备与关屏设置","Startup, display preparation and display-off settings");FooterHint.Text=T("更改将在保存后生效","Changes take effect after saving");}
        }

        private void OnLanguageChoice(object sender,RoutedEventArgs e)
        {
            if(!settingValues && IsLoaded && LanguageRequested!=null)LanguageRequested();
        }

        private void OnPrepareClick(object sender,RoutedEventArgs e){if(PrepareRequested!=null)PrepareRequested();}
        private void OnCancelPrepare(object sender,RoutedEventArgs e){if(CancelPrepareRequested!=null)CancelPrepareRequested();}
        private void OnDisplaySettings(object sender,RoutedEventArgs e){if(DisplaySettingsRequested!=null)DisplaySettingsRequested();}
        private void OnMigrationApply(object sender,RoutedEventArgs e)
        {
            int value;if(MigrationChoices.SelectedItem is int && MigrationRequested!=null){value=(int)MigrationChoices.SelectedItem;MigrationRequested(value);}
        }
        private void OnCheckClick(object sender,RoutedEventArgs e){if(RecheckRequested!=null)RecheckRequested();}
        private void OnRefreshPlayback(object sender,RoutedEventArgs e){if(RefreshRequested!=null)RefreshRequested();}
        private void OnRemoveWhitelist(object sender,RoutedEventArgs e){if(WhitelistRemoveRequested!=null)WhitelistRemoveRequested();}
        private void OnUpdateClick(object sender,RoutedEventArgs e){if(UpdateRequested!=null)UpdateRequested();}
        private void OnHelpClick(object sender,RoutedEventArgs e)
        {
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"帮助.html");
            if(File.Exists(path))Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
        }
        private void OnSaveClick(object sender,RoutedEventArgs e){if(SaveRequested!=null)SaveRequested();}
        private void OnCancelClick(object sender,RoutedEventArgs e){Close();}
        private void OnMinimize(object sender,RoutedEventArgs e){WindowState=WindowState.Minimized;}

        private void OnMinutesDown(object sender,RoutedEventArgs e){MinutesInput.Text=Math.Max(1,Minutes-1).ToString();}
        private void OnMinutesUp(object sender,RoutedEventArgs e){MinutesInput.Text=Math.Min(1440,Minutes+1).ToString();}
        private void OnProtectionMinutesDown(object sender,RoutedEventArgs e){ProtectionMinutesInput.Text=Math.Max(1,ProtectionMinutes-1).ToString();}
        private void OnProtectionMinutesUp(object sender,RoutedEventArgs e){ProtectionMinutesInput.Text=Math.Min(1440,ProtectionMinutes+1).ToString();}
        private void OnMinutesTextInput(object sender,TextCompositionEventArgs e){int ignored;e.Handled=!Int32.TryParse(e.Text,out ignored);}
        private void OnHotkeyGotFocus(object sender,KeyboardFocusChangedEventArgs e){HotkeyInput.SelectAll();}
        private void OnHotkeyPreviewKeyDown(object sender,KeyEventArgs e){e.Handled=true;}
        private void OnResetHotkey(object sender,RoutedEventArgs e){HotkeyInput.Text="Ctrl + Alt + F10";ShortcutEdited=true;}

        private void OnBackgroundChoice(object sender,RoutedEventArgs e)
        {
            var choice=sender as RadioButton;if(choice!=null)ApplyBackground(choice.Tag as string);
        }

        private void ApplyBackground(string mode)
        {
            backgroundMode=(mode??"geometry").ToLowerInvariant();
            if(backgroundMode!="geometry" && backgroundMode!="warm" && backgroundMode!="black")backgroundMode="geometry";
            bool dark=backgroundMode=="black";
            if(GeometryLayer==null)return;
            GeometryLayer.Visibility=backgroundMode=="geometry"?Visibility.Visible:Visibility.Collapsed;
            if(backgroundMode=="warm")Stage.Background=MakeBrush("#E8E5DF");
            else if(dark)Stage.Background=MakeBrush("#0D0E12");
            else Stage.Background=new LinearGradientBrush(ParseColor("#E8EDF3"),ParseColor("#CDD8E3"),45);
            GeometryChoice.IsChecked=backgroundMode=="geometry";WarmChoice.IsChecked=backgroundMode=="warm";BlackChoice.IsChecked=backgroundMode=="black";
            if(dark)
            {
                Resources["Text1"]=MakeBrush("#F2FFFFFF");Resources["Text2"]=MakeBrush("#A6FFFFFF");Resources["Text3"]=MakeBrush("#70FFFFFF");
                Resources["Stroke"]=MakeBrush("#18FFFFFF");Resources["FillControl"]=MakeBrush("#12FFFFFF");Resources["FillHover"]=MakeBrush("#0FFFFFFF");
                Resources["FillSelected"]=MakeBrush("#20FFFFFF");Resources["WarningSurface"]=MakeBrush("#2AF5A524");Resources["DangerSurface"]=MakeBrush("#28E5484D");Resources["SuccessSurface"]=MakeBrush("#2035B880");
                Shell.Background=MakeGradient("#F0181A1E","#F0121417");SidebarSurface.Background=MakeBrush("#18FFFFFF");FooterBar.Background=MakeBrush("#E614171B");Shell.BorderBrush=MakeBrush("#22FFFFFF");
            }
            else
            {
                Resources["Text1"]=MakeBrush("#EB141418");Resources["Text2"]=MakeBrush("#9E141418");Resources["Text3"]=MakeBrush("#6B141418");
                Resources["Stroke"]=MakeBrush("#14000000");Resources["FillControl"]=MakeBrush("#80FFFFFF");Resources["FillHover"]=MakeBrush("#0C000000");
                Resources["FillSelected"]=MakeBrush("#CFFFFFFF");Resources["WarningSurface"]=MakeBrush("#FFF8E8");Resources["DangerSurface"]=MakeBrush("#FFF0F0");Resources["SuccessSurface"]=MakeBrush("#EEF9F2");
                Shell.Background=MakeGradient("#DCF6F7F9","#D8EDF1F5");SidebarSurface.Background=MakeBrush("#16808080");FooterBar.Background=MakeBrush("#DCF4F5F7");Shell.BorderBrush=MakeBrush("#24000000");
            }
        }

        private static SolidColorBrush MakeBrush(string value)
        {
            var brush=new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));brush.Freeze();return brush;
        }
        private static Color ParseColor(string value){return (Color)ColorConverter.ConvertFromString(value);}
        private static LinearGradientBrush MakeGradient(string from,string to)
        {
            var brush=new LinearGradientBrush(ParseColor(from),ParseColor(to),90);brush.Freeze();return brush;
        }
        private static T FindAncestor<T>(DependencyObject value) where T:DependencyObject
        {
            while(value!=null){var found=value as T;if(found!=null)return found;value=VisualTreeHelper.GetParent(value);}
            return null;
        }
    }
}
