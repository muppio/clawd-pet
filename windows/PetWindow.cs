using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClawdPet;

// MARK: - The window
//
// A borderless, transparent, always-on-top window in the corner. With
// AllowsTransparency, Windows lets clicks on fully transparent pixels fall
// through to whatever is underneath, so only Clawd, his composer and his bubble
// take the mouse (Clawd gets an almost-invisible backdrop so the gaps between
// his legs still count). A left click on Clawd never activates the window
// (WM_MOUSEACTIVATE), so it opens Claude without stealing focus; clicking the
// composer or bubble does, so typing lands in Clawd and not the app underneath.

sealed class PetWindow : Window
{
    const int HotKeyId = 0xC1D;

    readonly PetModel m;
    IntPtr hwnd;

    readonly Grid petHost = new();
    readonly SpriteView sprite = new();
    readonly ScaleTransform squish = new(1, 1);
    readonly Border composerShadow = new();
    readonly Border composer = new();
    readonly TextBox input = new();
    readonly TextBlock placeholder = new();
    readonly TextBlock speakIcon = new();
    readonly TextBlock speakLabel = new();
    readonly TextBlock modelLabel = new();
    readonly TextBlock micIcon = new();
    readonly ScaleTransform micScale = new(1, 1);
    readonly VoiceOrb orb = new();
    readonly ContextMenu petMenu = new();

    readonly Grid bubbleHost = new();
    readonly ScaleTransform bubbleScale = new(1, 1);
    readonly TextBlock bubbleMark = new();
    readonly TextBlock bubbleStatus = new();
    readonly TextBlock bubblePrompt = new();
    readonly StackPanel bubbleDetailRow = new();
    readonly TextBlock bubbleDetail = new();
    readonly ScrollViewer bubbleScroll = new();
    readonly TextBlock bubbleBody = new();
    readonly Button copyButton;

    Guid? shownBubble;
    string renderedBody = "";
    bool hovering;
    bool dragMoved;
    Native.POINT dragCursor;
    Point dragOrigin;

    public PetWindow(PetModel model)
    {
        m = model;
        Title = "Clawd Pet";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Pet.WindowWidth;
        Height = Pet.WindowHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var origin = m.InitialOrigin();
        Left = origin.X;
        Top = origin.Y;
        UseLayoutRounding = true;

        copyButton = IconButton("", "Copy", m.CopyBubble);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(10, 0, 0, 10),
        };
        stack.Children.Add(BuildBubble());
        stack.Children.Add(BuildPetStack());
        var root = new Grid();
        root.Children.Add(stack);
        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            m.Escape();
            e.Handled = true;
        };

        m.Window = this;
        m.Changed += Refresh;
        m.Squished += PlaySquish;
        m.FocusComposer += FocusInput;
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) =>
        {
            if (hwnd != IntPtr.Zero) Native.UnregisterHotKey(hwnd, HotKeyId);
        };
        Refresh();
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        hwnd = new WindowInteropHelper(this).Handle;
        Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW); // not in Alt+Tab
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        // ⌃⌥C on the Mac. If another app already owns Ctrl+Alt+C, Clawd simply goes without.
        Native.RegisterHotKey(hwnd, HotKeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_C);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt64() == HotKeyId)
        {
            m.Summon();
            handled = true;
        }
        else if (msg == Native.WM_MOUSEACTIVATE)
        {
            int mouseMessage = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
            if (mouseMessage == Native.WM_LBUTTONDOWN && CursorOverPet())
            {
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            }
        }
        return IntPtr.Zero;
    }

    bool CursorOverPet()
    {
        if (!Native.GetCursorPos(out var pt) || !petHost.IsVisible) return false;
        try
        {
            var p = petHost.PointFromScreen(new Point(pt.X, pt.Y));
            return p.X >= 0 && p.Y >= 0 && p.X <= petHost.ActualWidth && p.Y <= petHost.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    // MARK: Building the views

    static ControlTemplate? flatTemplate;

    static ControlTemplate FlatTemplate()
    {
        if (flatTemplate != null) return flatTemplate;
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        t.VisualTree = border;
        t.Seal();
        return flatTemplate = t;
    }

    static Button FlatButton(UIElement content, string? tip, Action click)
    {
        var b = new Button
        {
            Content = content,
            Template = FlatTemplate(),
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tip != null) b.ToolTip = tip;
        b.Click += (_, _) => click();
        return b;
    }

    static TextBlock IconGlyph(string code, double size, Brush brush) => new()
    {
        Text = code,
        FontFamily = Palette.Icons,
        FontSize = size,
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Grid Square(double size, UIElement child)
    {
        var g = new Grid { Width = size, Height = size, Background = Brushes.Transparent };
        g.Children.Add(child);
        return g;
    }

    static Button IconButton(string code, string tip, Action click) =>
        FlatButton(Square(20, IconGlyph(code, 10.5, Palette.Dim)), tip, click);

    FrameworkElement BuildPetStack()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };

        // The shadow is its own layer so the composer's text stays crisp.
        foreach (var b in new[] { composerShadow, composer })
        {
            b.Width = Pet.ComposerWidth;
            b.Height = Pet.ComposerHeight;
            b.Margin = new Thickness(0, Pet.ComposerTop, 0, 0);
            b.HorizontalAlignment = HorizontalAlignment.Left;
            b.VerticalAlignment = VerticalAlignment.Top;
            b.CornerRadius = new CornerRadius(26);
            b.Background = Palette.PanelBrush;
        }
        composerShadow.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.38, BlurRadius = 16, ShadowDepth = 6, Direction = 270 };
        composer.BorderBrush = Palette.PanelStroke;
        composer.BorderThickness = new Thickness(1);
        composer.Child = BuildComposer();
        grid.Children.Add(composerShadow);
        grid.Children.Add(composer);

        petHost.Width = Pet.PetWidth;
        petHost.Height = Pet.PetHeight;
        petHost.Margin = new Thickness(Pet.PetInset, 0, 0, 0);
        petHost.HorizontalAlignment = HorizontalAlignment.Left;
        petHost.VerticalAlignment = VerticalAlignment.Top;
        petHost.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // catches clicks between his legs
        petHost.RenderTransformOrigin = new Point(0.5, 1);
        petHost.RenderTransform = squish;
        var canvas = new Canvas { ClipToBounds = false };
        Canvas.SetLeft(sprite, 0);
        Canvas.SetTop(sprite, -SpriteView.Headroom);
        canvas.Children.Add(sprite);
        petHost.Children.Add(canvas);

        petHost.MouseEnter += (_, _) =>
        {
            hovering = true;
            m.Poke();
            UpdateLook();
        };
        petHost.MouseLeave += (_, _) =>
        {
            hovering = false;
            UpdateLook();
        };
        petHost.MouseLeftButtonDown += PetDown;
        petHost.MouseMove += PetMove;
        petHost.MouseLeftButtonUp += PetUp;
        petHost.LostMouseCapture += (_, _) =>
        {
            if (!m.Dragging) return;
            m.Dragging = false;
            if (dragMoved) m.SavePosition(Left, Top);
        };
        petHost.ContextMenu = petMenu;
        petHost.ContextMenuOpening += (_, _) => FillPetMenu();
        grid.Children.Add(petHost);
        return grid;
    }

    FrameworkElement BuildComposer()
    {
        var g = new Grid();

        // The little grab pill under the pet, like the Codex composer has.
        g.Children.Add(new Border
        {
            Width = 22,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = Palette.Brush(Colors.White, 0.22),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Pet.PetCenterX - 11, 36, 0, 0),
            IsHitTestVisible = false,
        });

        input.Margin = new Thickness(Pet.TextInset, 14, 16, 0);
        input.VerticalAlignment = VerticalAlignment.Top;
        input.FontFamily = Palette.Mono;
        input.FontSize = 14;
        input.Foreground = Brushes.White;
        input.Background = Brushes.Transparent;
        input.BorderThickness = new Thickness(0);
        input.Padding = new Thickness(0);
        input.CaretBrush = Brushes.White;
        input.SelectionBrush = Palette.ClawdBrush;
        input.TextWrapping = TextWrapping.Wrap;
        input.AcceptsReturn = false;
        input.MinLines = 1;
        input.MaxLines = 2;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        input.FocusVisualStyle = null;
        input.TextChanged += (_, _) =>
        {
            if (input.Text != m.Draft) m.Draft = input.Text;
            placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            m.SubmitDraft();
            e.Handled = true;
        };

        placeholder.Margin = new Thickness(Pet.TextInset + 2, 14, 16, 0);
        placeholder.VerticalAlignment = VerticalAlignment.Top;
        placeholder.FontFamily = Palette.Mono;
        placeholder.FontSize = 14;
        placeholder.Foreground = Palette.Dim;
        placeholder.IsHitTestVisible = false;
        placeholder.TextTrimming = TextTrimming.CharacterEllipsis;
        g.Children.Add(input);
        g.Children.Add(placeholder);

        var row = new DockPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, 10, 10),
            LastChildFill = false,
            Height = 34,
        };

        var plus = FlatButton(Square(28, IconGlyph("", 14, Palette.White85)), "New chat", m.NewChat);
        DockPanel.SetDock(plus, Dock.Left);
        row.Children.Add(plus);

        speakIcon.FontFamily = Palette.Icons;
        speakIcon.FontSize = 12;
        speakIcon.VerticalAlignment = VerticalAlignment.Center;
        speakIcon.Margin = new Thickness(0, 1, 5, 0);
        speakLabel.FontFamily = Palette.Mono;
        speakLabel.FontSize = 12.5;
        speakLabel.VerticalAlignment = VerticalAlignment.Center;
        var speakContent = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        speakContent.Children.Add(speakIcon);
        speakContent.Children.Add(speakLabel);
        var speak = FlatButton(speakContent, null, () => m.SpeakReplies = !m.SpeakReplies);
        speak.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(speak, Dock.Left);
        row.Children.Add(speak);

        var orbButton = FlatButton(orb, "Talk with Claude", m.ToggleVoice);
        orbButton.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(orbButton, Dock.Right);
        row.Children.Add(orbButton);

        micIcon.FontFamily = Palette.Icons;
        micIcon.FontSize = 15;
        micIcon.Text = "";
        micIcon.HorizontalAlignment = HorizontalAlignment.Center;
        micIcon.VerticalAlignment = VerticalAlignment.Center;
        micIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        micIcon.RenderTransform = micScale;
        var mic = FlatButton(Square(28, micIcon), "Dictate", m.ToggleDictation);
        mic.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(mic, Dock.Right);
        row.Children.Add(mic);

        modelLabel.FontFamily = Palette.Mono;
        modelLabel.FontSize = 13;
        modelLabel.Foreground = Brushes.White;
        modelLabel.VerticalAlignment = VerticalAlignment.Center;
        var chevron = IconGlyph("", 8, Palette.Dim);
        chevron.Margin = new Thickness(5, 2, 0, 0);
        var modelContent = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        modelContent.Children.Add(modelLabel);
        modelContent.Children.Add(chevron);
        Button? modelButton = null;
        modelButton = FlatButton(modelContent, null, () => PopModelMenu(modelButton!));
        DockPanel.SetDock(modelButton, Dock.Right);
        row.Children.Add(modelButton);

        g.Children.Add(row);
        return g;
    }

    void PopModelMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        foreach (var mm in Enum.GetValues<ClaudeModel>())
        {
            var item = new MenuItem { Header = $"{mm.Label()}   {mm.Blurb()}", IsCheckable = true, IsChecked = m.Model == mm };
            item.Click += (_, _) => m.Model = mm;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    FrameworkElement BuildBubble()
    {
        bubbleHost.Width = 400;
        bubbleHost.HorizontalAlignment = HorizontalAlignment.Left;
        bubbleHost.Margin = new Thickness(0, 0, 0, 2);
        bubbleHost.Visibility = Visibility.Collapsed;
        bubbleHost.RenderTransformOrigin = new Point(0, 1);
        bubbleHost.RenderTransform = bubbleScale;

        // Shadow and fill on their own layers so the text stays crisp.
        var shadow = new BubbleBackdrop
        {
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.35, BlurRadius = 12, ShadowDepth = 4, Direction = 270 },
        };
        bubbleHost.Children.Add(shadow);
        bubbleHost.Children.Add(new BubbleBackdrop());

        var content = new StackPanel { Margin = new Thickness(13, 10, 13, 18) };

        var header = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(IconButton("", "Open Claude", m.OpenClaude));
        buttons.Children.Add(copyButton);
        buttons.Children.Add(IconButton("", "Dismiss", m.DismissBubble));
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);

        bubbleMark.Text = "✳";
        bubbleMark.FontFamily = Palette.Symbols;
        bubbleMark.FontSize = 14;
        bubbleMark.FontWeight = FontWeights.Bold;
        bubbleMark.VerticalAlignment = VerticalAlignment.Center;
        bubbleMark.Margin = new Thickness(0, 0, 6, 0);
        bubbleStatus.FontFamily = Palette.Mono;
        bubbleStatus.FontSize = 11.5;
        bubbleStatus.FontWeight = FontWeights.SemiBold;
        bubbleStatus.Foreground = Palette.White85;
        bubbleStatus.VerticalAlignment = VerticalAlignment.Center;
        bubbleStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        var title = new DockPanel();
        DockPanel.SetDock(bubbleMark, Dock.Left);
        title.Children.Add(bubbleMark);
        title.Children.Add(bubbleStatus);
        header.Children.Add(title);
        content.Children.Add(header);

        bubblePrompt.FontFamily = Palette.Mono;
        bubblePrompt.FontSize = 11.5;
        bubblePrompt.Foreground = Palette.Dim;
        bubblePrompt.TextWrapping = TextWrapping.Wrap;
        bubblePrompt.TextTrimming = TextTrimming.CharacterEllipsis;
        bubblePrompt.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        bubblePrompt.LineHeight = 15;
        bubblePrompt.MaxHeight = 30; // two lines
        bubblePrompt.Margin = new Thickness(0, 7, 0, 0);
        content.Children.Add(bubblePrompt);

        bubbleDetailRow.Orientation = Orientation.Horizontal;
        bubbleDetailRow.Margin = new Thickness(0, 7, 0, 0);
        bubbleDetailRow.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Palette.ClawdBrush, VerticalAlignment = VerticalAlignment.Center });
        bubbleDetail.FontFamily = Palette.Mono;
        bubbleDetail.FontSize = 12;
        bubbleDetail.Foreground = Palette.White80;
        bubbleDetail.TextTrimming = TextTrimming.CharacterEllipsis;
        bubbleDetail.MaxWidth = 355;
        bubbleDetail.Margin = new Thickness(6, 0, 0, 0);
        bubbleDetailRow.Children.Add(bubbleDetail);
        content.Children.Add(bubbleDetailRow);

        bubbleBody.FontFamily = Palette.Serif;
        bubbleBody.FontSize = 13.5;
        bubbleBody.Foreground = Palette.White93;
        bubbleBody.TextWrapping = TextWrapping.Wrap;
        bubbleBody.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        bubbleBody.LineHeight = 18;
        bubbleScroll.Content = bubbleBody;
        bubbleScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        bubbleScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        bubbleScroll.Margin = new Thickness(0, 7, 0, 0);
        bubbleScroll.Focusable = false;
        content.Children.Add(bubbleScroll);

        bubbleHost.Children.Add(content);
        return bubbleHost;
    }

    void FillPetMenu()
    {
        m.CheckCodexPet();
        petMenu.Items.Clear();

        void Add(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            petMenu.Items.Add(item);
        }

        void Check(ItemsControl parent, string header, bool on, Action<bool> set)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = on };
            item.Click += (_, _) => set(item.IsChecked);
            parent.Items.Add(item);
        }

        void Divider() => petMenu.Items.Add(new Separator());

        Add("Open Claude", m.OpenClaude);
        Add("New chat", m.NewChat);
        Divider();
        if (m.CodexPetOut) Add("Spar with Codex 🥊", () => m.StartFight());
        else Add("Spar with Codex: open the Codex pet first", () => { }, enabled: false);
        Check(petMenu, "Spar now and then", m.SparRandomly, v => m.SparRandomly = v);
        Divider();
        Add(m.ComposerOpen ? "Mini mode (hide composer)" : "Show composer", () => m.ComposerOpen = !m.ComposerOpen);
        Check(petMenu, "Speak replies", m.SpeakReplies, v => m.SpeakReplies = v);
        var models = new MenuItem { Header = "Model" };
        foreach (var mm in Enum.GetValues<ClaudeModel>())
            Check(models, $"{mm.Label()} · {mm.Blurb()}", m.Model == mm, on => { if (on) m.Model = mm; });
        petMenu.Items.Add(models);
        Divider();
        Add("Snap back to the corner", m.SnapHome);
        Check(petMenu, "Open at login", m.LoginEnabled, m.SetLogin);
        Divider();
        Add("Hide Clawd", () => Application.Current.Shutdown());
    }

    // MARK: Keeping the views in step with the model

    void UpdateLook() => sprite.Look = new ClawdLook(m.Mood, hovering, m.MouthOpen, m.ReduceMotion);

    void Refresh()
    {
        UpdateLook();
        sprite.Visibility = m.ClawdAway ? Visibility.Hidden : Visibility.Visible;

        var composerVisibility = m.ComposerOpen ? Visibility.Visible : Visibility.Collapsed;
        composer.Visibility = composerVisibility;
        composerShadow.Visibility = composerVisibility;

        if (input.Text != m.Draft)
        {
            input.Text = m.Draft;
            input.CaretIndex = input.Text.Length;
        }
        placeholder.Text = m.Placeholder;
        placeholder.Visibility = m.Draft.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (m.VoiceMode && input.IsKeyboardFocused) Keyboard.ClearFocus();

        speakIcon.Text = m.SpeakReplies ? "" : "";
        speakLabel.Text = m.SpeakReplies ? "Speak replies" : "Silent";
        var speakBrush = m.SpeakReplies ? Palette.White85 : Palette.Dim;
        speakIcon.Foreground = speakBrush;
        speakLabel.Foreground = speakBrush;
        modelLabel.Text = m.Model.Label();

        micIcon.Foreground = m.Dictating ? Palette.ClawdBrush : Palette.White85;
        double micGrow = m.Dictating ? 1 + m.Level * 0.25 : 1;
        micScale.ScaleX = micGrow;
        micScale.ScaleY = micGrow;
        orb.Set(m.VoiceMode, m.Level);

        RefreshBubble();
    }

    void RefreshBubble()
    {
        var b = m.Bubble;
        if (b == null)
        {
            if (shownBubble != null)
            {
                shownBubble = null;
                HideBubble();
            }
            return;
        }
        bool isNew = shownBubble != b.Id;
        shownBubble = b.Id;

        bubbleMark.Foreground = b.Kind == BubbleKind.Error ? Palette.YellowBrush : Palette.ClawdBrush;
        bubbleStatus.Text = b.Status;
        copyButton.Visibility = b.Body.Length == 0 && b.Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bubblePrompt.Text = b.Kind == BubbleKind.Code ? $"“{b.Prompt}”" : b.Prompt;
        bubblePrompt.Visibility = b.Prompt.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bubbleDetail.Text = b.Detail;
        bubbleDetailRow.Visibility = b.Detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (isNew || b.Body != renderedBody)
        {
            renderedBody = b.Body;
            bubbleBody.Inlines.Clear();
            Markdown.Render(b.Body, bubbleBody.Inlines);
            bubbleScroll.Visibility = b.Body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            // Long replies scroll (and follow the stream); the stack never outgrows the window.
            bubbleScroll.MaxHeight = b.Body.Length > 360 ? 170 : 190;
            if (b.Body.Length > 360) Dispatcher.InvokeAsync(bubbleScroll.ScrollToEnd, DispatcherPriority.Loaded);
        }
        if (isNew) ShowBubble();
    }

    void ShowBubble()
    {
        bubbleHost.BeginAnimation(OpacityProperty, null);
        bubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        bubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        bubbleHost.Visibility = Visibility.Visible;
        bubbleHost.Opacity = 1;
        if (m.ReduceMotion) return;
        var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        var grow = TimeSpan.FromSeconds(0.28);
        bubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, grow) { EasingFunction = ease });
        bubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, grow) { EasingFunction = ease });
        bubbleHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)));
    }

    void HideBubble()
    {
        if (m.ReduceMotion)
        {
            bubbleHost.BeginAnimation(OpacityProperty, null);
            bubbleHost.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.15));
        fade.Completed += (_, _) =>
        {
            if (shownBubble == null) bubbleHost.Visibility = Visibility.Collapsed;
        };
        bubbleHost.BeginAnimation(OpacityProperty, fade);
    }

    void PlaySquish()
    {
        AnimationTimeline Squash(double to) => new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.13)), new QuadraticEase { EasingMode = EasingMode.EaseOut }),
                new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.5)), new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }),
            },
        };
        squish.BeginAnimation(ScaleTransform.ScaleXProperty, Squash(1.14));
        squish.BeginAnimation(ScaleTransform.ScaleYProperty, Squash(0.84));
    }

    void FocusInput()
    {
        BringToFront();
        Activate();
        input.Focus();
        Keyboard.Focus(input);
        input.CaretIndex = input.Text.Length;
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (hwnd != IntPtr.Zero) Native.KeepOnTop(hwnd);
    }

    public void MoveTo(Point origin)
    {
        Left = origin.X;
        Top = origin.Y;
    }

    /// For sparring: Clawd's canvas top-left and his monitor, in physical
    /// pixels (the same units as the Codex pet's window), and the DPI scale.
    public (Point PetTopLeft, Native.RECT Monitor, double Scale)? FightFrame()
    {
        if (hwnd == IntPtr.Zero || !petHost.IsVisible) return null;
        try
        {
            var topLeft = petHost.PointToScreen(new Point(0, 0));
            return (topLeft, Native.MonitorRect(hwnd), VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
        catch
        {
            return null;
        }
    }

    // MARK: Click to open Claude, drag to move

    void PetDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Native.GetCursorPos(out dragCursor);
        dragOrigin = new Point(Left, Top);
        dragMoved = false;
        m.Dragging = true;
        petHost.CaptureMouse();
    }

    void PetMove(object sender, MouseEventArgs e)
    {
        if (!m.Dragging || !petHost.IsMouseCaptured) return;
        Native.GetCursorPos(out var now);
        double dx = now.X - dragCursor.X, dy = now.Y - dragCursor.Y;
        if (!dragMoved && Math.Sqrt(dx * dx + dy * dy) > 3) dragMoved = true;
        if (!dragMoved) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = dragOrigin.X + dx / dpi.DpiScaleX;
        Top = dragOrigin.Y + dy / dpi.DpiScaleY;
    }

    void PetUp(object sender, MouseButtonEventArgs e)
    {
        if (!m.Dragging) return;
        e.Handled = true;
        m.Dragging = false;
        petHost.ReleaseMouseCapture();
        if (dragMoved) m.SavePosition(Left, Top);
        else m.PetClicked();
    }
}

/// The speech bubble's shape: a rounded box with a little tail pointing down at Clawd.
sealed class BubbleBackdrop : FrameworkElement
{
    static readonly Brush Fill = Palette.Brush(Palette.PanelFill, 0.98);
    static readonly Pen Stroke = MakeStroke();

    static Pen MakeStroke()
    {
        var p = new Pen(Palette.PanelStroke, 1);
        p.Freeze();
        return p;
    }

    public BubbleBackdrop() => IsHitTestVisible = true;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, tail = 8, tx = Pet.PetCenterX;
        if (w <= 2 || h <= tail + 2) return;
        var body = new RectangleGeometry(new Rect(0.5, 0.5, w - 1, h - tail - 1), 16, 16);
        var tip = new StreamGeometry();
        using (var c = tip.Open())
        {
            c.BeginFigure(new Point(tx - 8, h - tail - 1.5), true, true);
            c.LineTo(new Point(tx, h - 0.5), true, false);
            c.LineTo(new Point(tx + 8, h - tail - 1.5), true, false);
        }
        dc.DrawGeometry(Fill, Stroke, new CombinedGeometry(GeometryCombineMode.Union, body, tip));
    }
}

/// The voice orb: white with a waveform at rest, Clawd orange with live level
/// bars and a pulsing ring while voice mode is on.
sealed class VoiceOrb : FrameworkElement
{
    static readonly double[] Wobble = { 0.55, 1.0, 0.8, 0.45 };
    static readonly Pen Ring = MakeRing();
    bool active;
    double level;

    static Pen MakeRing()
    {
        var p = new Pen(Palette.Brush(Palette.Clawd, 0.55), 2);
        p.Freeze();
        return p;
    }

    public VoiceOrb()
    {
        Width = 34;
        Height = 34;
    }

    public void Set(bool isActive, double newLevel)
    {
        if (isActive == active && Math.Abs(newLevel - level) < 0.01) return;
        active = isActive;
        level = newLevel;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(17, 17);
        if (active)
        {
            double r = 16 * (1 + level * 0.55);
            dc.DrawEllipse(null, Ring, c, r, r);
        }
        dc.DrawEllipse(active ? Palette.ClawdBrush : Brushes.White, null, c, 17, 17);
        Brush bar = active ? Brushes.White : Brushes.Black;
        const double barWidth = 2.6, gap = 2.5;
        double x = 17 - (4 * barWidth + 3 * gap) / 2;
        for (int i = 0; i < 4; i++)
        {
            double h = active ? 5 + 11 * Math.Max(0.15, level) * Wobble[i] : 4 + 10 * Wobble[i];
            dc.DrawRoundedRectangle(bar, null, new Rect(x, 17 - h / 2, barWidth, h), barWidth / 2, barWidth / 2);
            x += barWidth + gap;
        }
    }
}

/// Bold, italics, `code` and links render; anything half-streamed stays literal.
static class Markdown
{
    static readonly Regex Token = new(
        @"\*\*(?<b>[^*\n]+?)\*\*" +
        @"|`(?<c>[^`\n]+)`" +
        @"|\[(?<lt>[^\]\n]+)\]\((?<lu>[^)\s]+)\)" +
        @"|(?<![\w*])\*(?<i>[^*\n]+?)\*(?![\w*])" +
        @"|(?<![\w_])_(?<u>[^_\n]+?)_(?![\w_])",
        RegexOptions.Compiled);

    static readonly Brush CodeBack = Palette.Brush(Colors.White, 0.08);

    public static void Render(string text, InlineCollection inlines)
    {
        int pos = 0;
        foreach (Match mt in Token.Matches(text))
        {
            if (mt.Index > pos) inlines.Add(new Run(text[pos..mt.Index]));
            if (mt.Groups["b"].Success)
            {
                inlines.Add(new Bold(new Run(mt.Groups["b"].Value)));
            }
            else if (mt.Groups["c"].Success)
            {
                inlines.Add(new Run(mt.Groups["c"].Value) { FontFamily = Palette.Mono, FontSize = 12.5, Background = CodeBack });
            }
            else if (mt.Groups["lt"].Success)
            {
                var link = new Hyperlink(new Run(mt.Groups["lt"].Value)) { Foreground = Palette.ClawdLightBrush };
                if (Uri.TryCreate(mt.Groups["lu"].Value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    link.NavigateUri = uri;
                    link.RequestNavigate += (_, e) =>
                    {
                        Open(e.Uri);
                        e.Handled = true;
                    };
                }
                inlines.Add(link);
            }
            else if (mt.Groups["i"].Success)
            {
                inlines.Add(new Italic(new Run(mt.Groups["i"].Value)));
            }
            else if (mt.Groups["u"].Success)
            {
                inlines.Add(new Italic(new Run(mt.Groups["u"].Value)));
            }
            pos = mt.Index + mt.Length;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
    }

    static void Open(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Pet.Log(ex);
        }
    }
}
