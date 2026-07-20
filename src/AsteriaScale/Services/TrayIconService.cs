using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using AsteriaScale.Models;

namespace AsteriaScale.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _automationItem;
    private readonly Forms.ToolStripMenuItem _translationItem;
    private readonly Forms.ToolStripMenuItem _smartModeItem;
    private readonly Forms.ToolStripMenuItem _neuralModeItem;
    private readonly Forms.ToolStripMenuItem _dictionaryModeItem;
    private readonly Forms.ToolStripMenuItem _screenModeItem;
    private readonly Action _showWindow;
    private readonly Func<Task> _scaleLastWindow;
    private readonly Action<bool> _setAutomation;
    private readonly Action<bool> _setHoverTranslation;
    private readonly Action<string> _setTranslationMode;
    private readonly Action _exit;
    private bool _updatingAutomation;
    private bool _updatingTranslation;

    public TrayIconService(
        bool automationEnabled,
        bool hoverTranslationEnabled,
        string hoverTranslationMode,
        Action showWindow,
        Func<Task> scaleLastWindow,
        Action<bool> setAutomation,
        Action<bool> setHoverTranslation,
        Action<string> setTranslationMode,
        Action exit)
    {
        _showWindow = showWindow;
        _scaleLastWindow = scaleLastWindow;
        _setAutomation = setAutomation;
        _setHoverTranslation = setHoverTranslation;
        _setTranslationMode = setTranslationMode;
        _exit = exit;

        Forms.ContextMenuStrip menu = new();
        Forms.ToolStripMenuItem openItem = new("Открыть Asteria Scale");
        openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => _showWindow();

        Forms.ToolStripMenuItem scaleItem = new("Масштабировать последнее окно");
        scaleItem.Click += async (_, _) => await SafeScaleAsync();

        _automationItem = new Forms.ToolStripMenuItem("Автоматизация")
        {
            Checked = automationEnabled,
            CheckOnClick = true
        };
        _automationItem.CheckedChanged += OnAutomationCheckedChanged;

        _translationItem = new Forms.ToolStripMenuItem("Перевод текста")
        {
            Checked = hoverTranslationEnabled,
            CheckOnClick = true
        };
        _translationItem.CheckedChanged += OnTranslationCheckedChanged;

        Forms.ToolStripMenuItem translationModeItem = new("Режим перевода");
        _smartModeItem = new Forms.ToolStripMenuItem("Умный словарь — выбор по контексту");
        _neuralModeItem = new Forms.ToolStripMenuItem("Нейросетевой — вся реплика");
        _dictionaryModeItem = new Forms.ToolStripMenuItem("Словарный — слово или выражение");
        _screenModeItem = new Forms.ToolStripMenuItem("Экран по клавише — Alt+Shift+T");
        _smartModeItem.Click += (_, _) => _setTranslationMode(HoverTranslationModes.Smart);
        _neuralModeItem.Click += (_, _) => _setTranslationMode(HoverTranslationModes.Neural);
        _dictionaryModeItem.Click += (_, _) => _setTranslationMode(HoverTranslationModes.Dictionary);
        _screenModeItem.Click += (_, _) => _setTranslationMode(HoverTranslationModes.Screen);
        translationModeItem.DropDownItems.Add(_smartModeItem);
        translationModeItem.DropDownItems.Add(_neuralModeItem);
        translationModeItem.DropDownItems.Add(_dictionaryModeItem);
        translationModeItem.DropDownItems.Add(_screenModeItem);
        SetTranslationMode(hoverTranslationMode);

        Forms.ToolStripMenuItem exitItem = new("Выход");
        exitItem.Click += (_, _) => _exit();

        menu.Items.Add(openItem);
        menu.Items.Add(scaleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_automationItem);
        menu.Items.Add(_translationItem);
        menu.Items.Add(translationModeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "Asteria Scale",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => _showWindow();
    }

    public void SetAutomationChecked(bool enabled)
    {
        if (_automationItem.Checked == enabled)
        {
            return;
        }

        _updatingAutomation = true;
        try
        {
            _automationItem.Checked = enabled;
        }
        finally
        {
            _updatingAutomation = false;
        }
    }

    public void SetHoverTranslationChecked(bool enabled)
    {
        if (_translationItem.Checked == enabled)
        {
            return;
        }

        _updatingTranslation = true;
        try
        {
            _translationItem.Checked = enabled;
        }
        finally
        {
            _updatingTranslation = false;
        }
    }

    public void SetTranslationMode(string mode)
    {
        string normalized = HoverTranslationModes.Normalize(mode);
        _smartModeItem.Checked = normalized == HoverTranslationModes.Smart;
        _neuralModeItem.Checked = normalized == HoverTranslationModes.Neural;
        _dictionaryModeItem.Checked = normalized == HoverTranslationModes.Dictionary;
        _screenModeItem.Checked = normalized == HoverTranslationModes.Screen;
    }

    private void OnAutomationCheckedChanged(object? sender, EventArgs e)
    {
        if (!_updatingAutomation)
        {
            _setAutomation(_automationItem.Checked);
        }
    }

    private void OnTranslationCheckedChanged(object? sender, EventArgs e)
    {
        if (!_updatingTranslation)
        {
            _setHoverTranslation(_translationItem.Checked);
        }
    }

    public void ShowHint(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3500);
    }

    private async Task SafeScaleAsync()
    {
        try
        {
            await _scaleLastWindow();
        }
        catch (Exception ex)
        {
            ShowHint("Asteria Scale", ex.Message);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
