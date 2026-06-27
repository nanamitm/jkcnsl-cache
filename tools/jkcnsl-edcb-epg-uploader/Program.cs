using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using System.ComponentModel;
using EpgTimer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
        => MainAsync(args).GetAwaiter().GetResult();

    private static async Task<int> MainAsync(string[] args)
    {
        var paths = AppPathResolver.Discover();
        var configStore = new ConfigStore(paths);
        var logger = new AppLogger(paths.LogDirectory);
        var worker = new UploadWorker(configStore, logger);

        try
        {
            var option = CommandLineOptions.Parse(args);
            var config = configStore.Current;
            ConsoleMode.EnsureFor(option, config);

            if (option.InstallAutostart)
            {
                var result = await AutostartManager.InstallAsync(config, paths.ExecutablePath);
                logger.Info(result);
                Console.WriteLine(result);
                return 0;
            }

            if (option.UninstallAutostart)
            {
                var result = await AutostartManager.UninstallAsync(config);
                logger.Info(result);
                Console.WriteLine(result);
                return 0;
            }

            if (option.ListServices)
            {
                using var client = new EdcbEpgClient(config.Edcb);
                foreach (var service in client.GetServices())
                {
                    Console.WriteLine($"{service.service_name} ONID={service.ONID} TSID={service.TSID} SID={service.SID}");
                }
                return 0;
            }

            if (option.Watch)
            {
                using var mutexHandle = SingleInstanceGuard.TryAcquire(config.Scheduler.MutexName);
                if (mutexHandle is null)
                {
                    Console.Error.WriteLine("すでに常駐起動中です。");
                    return 1;
                }

                if (config.Scheduler.UseTrayIcon)
                {
                    if (config.Scheduler.HideConsoleWindow)
                    {
                        ConsoleWindow.Hide();
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    using var scheduler = new UploadScheduler(configStore, worker, logger);
                    using var context = new TrayApplicationContext(configStore, logger, scheduler, paths.ExecutablePath);
                    scheduler.Start();
                    Application.Run(context);
                    return 0;
                }

                using var consoleScheduler = new UploadScheduler(configStore, worker, logger);
                consoleScheduler.Start();
                Console.WriteLine("常駐モードで起動しました。Ctrl+C で終了します。");
                await WaitForCancellationAsync();
                return 0;
            }

            var uploadResult = await worker.ExecuteAsync(option.DryRun, option.Channel);
            foreach (var line in uploadResult.Messages)
            {
                Console.WriteLine(line);
            }

            return uploadResult.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            logger.Error("実行中に未処理例外が発生しました。", ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task WaitForCancellationAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler? handler = null;
        handler = (_, e) =>
        {
            e.Cancel = true;
            Console.CancelKeyPress -= handler;
            tcs.TrySetResult();
        };
        Console.CancelKeyPress += handler;
        await tcs.Task;
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ConfigStore _configStore;
    private readonly AppLogger _logger;
    private readonly UploadScheduler _scheduler;
    private readonly string _executablePath;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _lastRunItem;
    private SettingsForm? _settingsForm;
    private LogViewerForm? _logViewerForm;

    public TrayApplicationContext(
        ConfigStore configStore,
        AppLogger logger,
        UploadScheduler scheduler,
        string executablePath)
    {
        _configStore = configStore;
        _logger = logger;
        _scheduler = scheduler;
        _executablePath = executablePath;

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("状態: 起動中") { Enabled = false };
        _lastRunItem = new ToolStripMenuItem("最終送信: まだありません") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(_lastRunItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("設定", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("ログ", null, (_, _) => OpenLogViewer()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("今すぐ送信", null, (_, _) => _scheduler.TriggerNow()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("終了", null, (_, _) => ExitThread()));

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIconLoader.Load(_configStore.Current, _executablePath),
            Text = "jkcnsl-edcb-epg-uploader",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings();

        _scheduler.StatusChanged += OnSchedulerStatusChanged;
        _logger.Info("トレイ常駐モードで起動しました。");
    }

    protected override void ExitThreadCore()
    {
        _scheduler.StatusChanged -= OnSchedulerStatusChanged;
        _scheduler.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _settingsForm?.Close();
        _logViewerForm?.Close();
        base.ExitThreadCore();
    }

    private void OnSchedulerStatusChanged(SchedulerStatus status)
    {
        if (_notifyIcon.ContextMenuStrip is null)
        {
            return;
        }

        if (_notifyIcon.ContextMenuStrip.InvokeRequired)
        {
            _notifyIcon.ContextMenuStrip.BeginInvoke(() => OnSchedulerStatusChanged(status));
            return;
        }

        _statusItem.Text = status.IsRunning
            ? $"状態: 送信中 ({status.Reason})"
            : $"状態: 待機中 次回 {FormatDateTime(status.NextScheduledAt)}";

        _lastRunItem.Text = status.LastCompletedAt is null
            ? "最終送信: まだありません"
            : $"最終送信: {FormatDateTime(status.LastCompletedAt)} ({status.LastMessage ?? "完了"})";
    }

    private void OpenSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_configStore, _logger, _scheduler, _executablePath);
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show();
            return;
        }

        _settingsForm.BringToFront();
    }

    private void OpenLogViewer()
    {
        if (_logViewerForm is null || _logViewerForm.IsDisposed)
        {
            _logViewerForm = new LogViewerForm(_logger);
            _logViewerForm.FormClosed += (_, _) => _logViewerForm = null;
            _logViewerForm.Show();
            return;
        }

        _logViewerForm.BringToFront();
    }

    private static string FormatDateTime(DateTimeOffset? value)
        => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
}

internal sealed class SettingsForm : Form
{
    private readonly ConfigStore _configStore;
    private readonly AppLogger _logger;
    private readonly UploadScheduler _scheduler;
    private readonly string _executablePath;
    private readonly TextBox _baseUrlTextBox;
    private readonly TextBox _apiKeyTextBox;
    private readonly NumericUpDown _intervalMinutes;
    private readonly NumericUpDown _startupDelaySeconds;
    private readonly CheckBox _runImmediatelyCheckBox;
    private readonly CheckBox _useTrayIconCheckBox;
    private readonly CheckBox _hideConsoleWindowCheckBox;
    private readonly BindingList<ServiceMappingRow> _serviceMappings;
    private readonly DataGridView _serviceMappingsGrid;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly System.Windows.Forms.Timer _statusTimer;
    private DateTimeOffset? _lastHandledManualCompletionAt;

    public SettingsForm(ConfigStore configStore, AppLogger logger, UploadScheduler scheduler, string executablePath)
    {
        _configStore = configStore;
        _logger = logger;
        _scheduler = scheduler;
        _executablePath = executablePath;

        Text = "設定 - jkcnsl-edcb-epg-uploader";
        Icon = AppIconLoader.Load(_configStore.Current, _executablePath);
        Width = 980;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var config = _configStore.Current;
        _serviceMappings = new BindingList<ServiceMappingRow>(config.ServiceMappings
            .Select(ServiceMappingRow.FromModel)
            .ToList());

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
            AutoSize = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var settingsGroup = new GroupBox
        {
            Text = "基本設定",
            Dock = DockStyle.Fill,
            AutoSize = true
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(12),
            AutoSize = true
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _baseUrlTextBox = AddTextBoxRow(table, 0, "API URL", config.ImportApi.BaseUrl);
        _apiKeyTextBox = AddTextBoxRow(table, 1, "API Key", config.ImportApi.ApiKey);
        _intervalMinutes = AddNumericRow(table, 2, "送信間隔(分)", config.Scheduler.IntervalMinutes, 1, 1440);
        _startupDelaySeconds = AddNumericRow(table, 3, "起動待機(秒)", config.Scheduler.StartupDelaySeconds, 0, 3600);
        _runImmediatelyCheckBox = AddCheckBoxRow(table, 4, "起動直後に送信", config.Scheduler.RunImmediately);
        _useTrayIconCheckBox = AddCheckBoxRow(table, 5, "トレイ常駐を使う", config.Scheduler.UseTrayIcon);
        _hideConsoleWindowCheckBox = AddCheckBoxRow(table, 6, "トレイ時にコンソールを隠す", config.Scheduler.HideConsoleWindow);
        settingsGroup.Controls.Add(table);

        var mappingGroup = new GroupBox
        {
            Text = "送信チャンネル設定",
            Dock = DockStyle.Fill
        };

        var mappingLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        mappingLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mappingLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var mappingButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        var addButton = new Button { Text = "追加", AutoSize = true };
        addButton.Click += (_, _) => _serviceMappings.Add(new ServiceMappingRow { Enabled = true });
        mappingButtons.Controls.Add(addButton);

        var removeMappingButton = new Button { Text = "削除", AutoSize = true };
        removeMappingButton.Click += (_, _) => RemoveSelectedMappings();
        mappingButtons.Controls.Add(removeMappingButton);

        var upButton = new Button { Text = "上へ", AutoSize = true };
        upButton.Click += (_, _) => MoveSelectedMapping(-1);
        mappingButtons.Controls.Add(upButton);

        var downButton = new Button { Text = "下へ", AutoSize = true };
        downButton.Click += (_, _) => MoveSelectedMapping(1);
        mappingButtons.Controls.Add(downButton);

        var addDefaultsButton = new Button { Text = "既定値を追加", AutoSize = true };
        addDefaultsButton.Click += (_, _) => AddDefaultMappings();
        mappingButtons.Controls.Add(addDefaultsButton);

        var importFromEdcbButton = new Button { Text = "EDCBから候補取得", AutoSize = true };
        importFromEdcbButton.Click += async (_, _) => await ImportFromEdcbAsync();
        mappingButtons.Controls.Add(importFromEdcbButton);

        var autofillVideoButton = new Button { Text = "videoを自動補完", AutoSize = true };
        autofillVideoButton.Click += (_, _) => AutofillVideoNames();
        mappingButtons.Controls.Add(autofillVideoButton);

        _serviceMappingsGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            DataSource = _serviceMappings
        };
        _serviceMappingsGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(ServiceMappingRow.Enabled),
            HeaderText = "有効",
            Width = 55
        });
        _serviceMappingsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ServiceMappingRow.Video),
            HeaderText = "video",
            Width = 110
        });
        _serviceMappingsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ServiceMappingRow.Onid),
            HeaderText = "ONID",
            Width = 90
        });
        _serviceMappingsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ServiceMappingRow.Tsid),
            HeaderText = "TSID",
            Width = 90
        });
        _serviceMappingsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ServiceMappingRow.Sid),
            HeaderText = "SID",
            Width = 90
        });
        _serviceMappingsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(ServiceMappingRow.Memo),
            HeaderText = "メモ",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        _serviceMappingsGrid.CellEndEdit += OnServiceMappingsCellEndEdit;

        mappingLayout.Controls.Add(mappingButtons, 0, 0);
        mappingLayout.Controls.Add(_serviceMappingsGrid, 0, 1);
        mappingGroup.Controls.Add(mappingLayout);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true
        };

        var saveButton = new Button { Text = "保存", AutoSize = true };
        saveButton.Click += (_, _) => SaveSettings();
        buttons.Controls.Add(saveButton);

        var sendNowButton = new Button { Text = "今すぐ送信", AutoSize = true };
        sendNowButton.Click += (_, _) =>
        {
            if (!TryBuildManualSendConfig(out var manualConfig))
            {
                return;
            }

            ShowStatus("送信を開始しました。");
            _scheduler.TriggerNow(manualConfig);
        };
        buttons.Controls.Add(sendNowButton);

        var installButton = new Button { Text = "自動起動を登録", AutoSize = true };
        installButton.Click += async (_, _) =>
        {
            try
            {
                var message = await AutostartManager.InstallAsync(_configStore.Current, _executablePath);
                _logger.Info(message);
                ShowStatus(message);
            }
            catch (Exception ex)
            {
                _logger.Error("自動起動登録に失敗しました。", ex);
                MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        buttons.Controls.Add(installButton);

        var removeButton = new Button { Text = "自動起動を削除", AutoSize = true };
        removeButton.Click += async (_, _) =>
        {
            try
            {
                var message = await AutostartManager.UninstallAsync(_configStore.Current);
                _logger.Info(message);
                ShowStatus(message);
            }
            catch (Exception ex)
            {
                _logger.Error("自動起動削除に失敗しました。", ex);
                MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        buttons.Controls.Add(removeButton);

        var statusStrip = new StatusStrip
        {
            Dock = DockStyle.Fill,
            SizingGrip = false
        };
        _statusLabel = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleRight
        };
        statusStrip.Items.Add(_statusLabel);
        _statusTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            _statusLabel.Text = string.Empty;
        };

        root.Controls.Add(settingsGroup, 0, 0);
        root.Controls.Add(mappingGroup, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        root.Controls.Add(statusStrip, 0, 3);
        Controls.Add(root);

        _scheduler.StatusChanged += OnSchedulerStatusChanged;
        FormClosed += (_, _) => _scheduler.StatusChanged -= OnSchedulerStatusChanged;
    }

    private void SaveSettings()
    {
        _serviceMappingsGrid.EndEdit();

        if (!TryBuildEditableConfig(out var updated))
        {
            return;
        }

        _configStore.Update(updated, saveLocalSettings: true);
        _scheduler.ReloadSchedule();
        _logger.Info("設定を保存しました。");
        ShowStatus("local/appsettings.json に保存しました。");
    }

    private bool TryBuildManualSendConfig(out AppConfig config)
    {
        if (!TryBuildEditableConfig(out config))
        {
            return false;
        }

        return true;
    }

    private bool TryBuildEditableConfig(out AppConfig config)
    {
        if (!Uri.TryCreate(_baseUrlTextBox.Text.Trim(), UriKind.Absolute, out _))
        {
            MessageBox.Show("API URL の形式が不正です。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            config = null!;
            return false;
        }

        if (!TryBuildServiceMappings(out var serviceMappings, out var validationMessage))
        {
            MessageBox.Show(validationMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            config = null!;
            return false;
        }

        config = _configStore.Current.DeepClone();
        config.ImportApi.BaseUrl = _baseUrlTextBox.Text.Trim();
        config.ImportApi.ApiKey = _apiKeyTextBox.Text.Trim();
        config.Scheduler.IntervalMinutes = (int)_intervalMinutes.Value;
        config.Scheduler.StartupDelaySeconds = (int)_startupDelaySeconds.Value;
        config.Scheduler.RunImmediately = _runImmediatelyCheckBox.Checked;
        config.Scheduler.UseTrayIcon = _useTrayIconCheckBox.Checked;
        config.Scheduler.HideConsoleWindow = _hideConsoleWindowCheckBox.Checked;
        config.ServiceMappings = serviceMappings;
        return true;
    }

    private static TextBox AddTextBoxRow(TableLayoutPanel table, int row, string label, string value)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var textBox = new TextBox { Text = value, Dock = DockStyle.Fill };
        table.Controls.Add(textBox, 1, row);
        return textBox;
    }

    private static NumericUpDown AddNumericRow(TableLayoutPanel table, int row, string label, int value, int min, int max)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var control = new NumericUpDown
        {
            Dock = DockStyle.Left,
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Width = 120
        };
        table.Controls.Add(control, 1, row);
        return control;
    }

    private static CheckBox AddCheckBoxRow(TableLayoutPanel table, int row, string label, bool value)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        var checkBox = new CheckBox { Checked = value, AutoSize = true, Dock = DockStyle.Left };
        table.Controls.Add(checkBox, 1, row);
        return checkBox;
    }

    private void ShowStatus(string message)
    {
        _statusLabel.Text = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void OnSchedulerStatusChanged(SchedulerStatus status)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => OnSchedulerStatusChanged(status));
            return;
        }

        if (!string.Equals(status.Reason, "manual", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (status.IsRunning)
        {
            ShowStatus("送信中です...");
            return;
        }

        if (status.LastCompletedAt is null || status.LastCompletedAt == _lastHandledManualCompletionAt)
        {
            return;
        }

        _lastHandledManualCompletionAt = status.LastCompletedAt;
        ShowStatus(status.LastMessage == "成功" ? "送信が完了しました。" : "送信に失敗しました。ログを確認してください。");
    }

    private void RemoveSelectedMappings()
    {
        if (_serviceMappingsGrid.CurrentRow?.DataBoundItem is not ServiceMappingRow row)
        {
            return;
        }

        _serviceMappings.Remove(row);
    }

    private void MoveSelectedMapping(int offset)
    {
        if (_serviceMappingsGrid.CurrentRow?.DataBoundItem is not ServiceMappingRow row)
        {
            return;
        }

        var oldIndex = _serviceMappings.IndexOf(row);
        var newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= _serviceMappings.Count)
        {
            return;
        }

        _serviceMappings.RaiseListChangedEvents = false;
        _serviceMappings.RemoveAt(oldIndex);
        _serviceMappings.Insert(newIndex, row);
        _serviceMappings.RaiseListChangedEvents = true;
        _serviceMappings.ResetBindings();
        _serviceMappingsGrid.ClearSelection();
        _serviceMappingsGrid.Rows[newIndex].Selected = true;
        _serviceMappingsGrid.CurrentCell = _serviceMappingsGrid.Rows[newIndex].Cells[0];
    }

    private void AddDefaultMappings()
    {
        foreach (var mapping in GetDefaultServiceMappings())
        {
            if (_serviceMappings.Any(x => x.Video.Equals(mapping.Video, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _serviceMappings.Add(ServiceMappingRow.FromModel(mapping));
        }
    }

    private async Task ImportFromEdcbAsync()
    {
        try
        {
            using var client = new EdcbEpgClient(_configStore.Current.Edcb);
            var candidates = client.GetServices()
                .Select(x => ServiceCandidate.FromService(x))
                .OrderBy(x => x.ServiceName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            using var dialog = new ServiceSelectionForm(candidates);
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            foreach (var candidate in dialog.SelectedCandidates)
            {
                if (_serviceMappings.Any(x => x.Onid == candidate.Onid && x.Tsid == candidate.Tsid && x.Sid == candidate.Sid))
                {
                    continue;
                }

                _serviceMappings.Add(new ServiceMappingRow
                {
                    Enabled = true,
                    Video = SuggestVideoName(candidate.ServiceName, candidate.Sid),
                    Onid = candidate.Onid,
                    Tsid = candidate.Tsid,
                    Sid = candidate.Sid,
                    Memo = candidate.ServiceName
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error("EDCBから候補取得に失敗しました。", ex);
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        await Task.CompletedTask;
    }

    private bool TryBuildServiceMappings(out List<ServiceMapping> mappings, out string message)
    {
        mappings = [];
        message = string.Empty;
        var seenVideos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTriples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _serviceMappings)
        {
            if (string.IsNullOrWhiteSpace(row.Video) &&
                row.Onid == 0 &&
                row.Tsid == 0 &&
                row.Sid == 0 &&
                string.IsNullOrWhiteSpace(row.Memo))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.Video))
            {
                message = "送信チャンネル設定の video は必須です。";
                return false;
            }

            if (row.Onid < 0 || row.Onid > ushort.MaxValue ||
                row.Tsid < 0 || row.Tsid > ushort.MaxValue ||
                row.Sid < 0 || row.Sid > ushort.MaxValue)
            {
                message = "ONID / TSID / SID は 0 から 65535 の範囲で入力してください。";
                return false;
            }

            if (!seenVideos.Add(row.Video.Trim()))
            {
                message = $"video が重複しています: {row.Video}";
                return false;
            }

            var tripleKey = $"{row.Onid}:{row.Tsid}:{row.Sid}";
            if (!seenTriples.Add(tripleKey))
            {
                message = $"ONID / TSID / SID の組み合わせが重複しています: {tripleKey}";
                return false;
            }

            mappings.Add(new ServiceMapping
            {
                Enabled = row.Enabled,
                Video = row.Video.Trim(),
                Onid = (ushort)row.Onid,
                Tsid = (ushort)row.Tsid,
                Sid = (ushort)row.Sid,
                Memo = row.Memo?.Trim() ?? string.Empty
            });
        }

        if (mappings.Count == 0)
        {
            message = "送信チャンネル設定を1件以上入力してください。";
            return false;
        }

        return true;
    }

    private static List<ServiceMapping> GetDefaultServiceMappings()
        => new()
        {
            new ServiceMapping { Enabled = true, Video = "jk171", Onid = 4, Tsid = 16402, Sid = 171, Memo = "ＢＳテレ東" },
            new ServiceMapping { Enabled = true, Video = "jk172", Onid = 4, Tsid = 16402, Sid = 172, Memo = "ＢＳテレ東２" },
            new ServiceMapping { Enabled = true, Video = "jk173", Onid = 4, Tsid = 16402, Sid = 173, Memo = "ＢＳテレ東３" }
        };

    private void AutofillVideoNames()
    {
        var updatedCount = 0;
        foreach (var row in _serviceMappings)
        {
            if (!string.IsNullOrWhiteSpace(row.Video))
            {
                continue;
            }

            if (row.Sid <= 0)
            {
                continue;
            }

            row.Video = SuggestVideoName(row.Memo, row.Sid);
            updatedCount++;
        }

        _serviceMappings.ResetBindings();
        ShowStatus(updatedCount > 0
            ? $"{updatedCount} 件の video を自動補完しました。"
            : "自動補完できる video はありませんでした。");
    }

    private void OnServiceMappingsCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _serviceMappings.Count)
        {
            return;
        }

        var row = _serviceMappings[e.RowIndex];
        if (!string.IsNullOrWhiteSpace(row.Video) || row.Sid <= 0)
        {
            return;
        }

        row.Video = SuggestVideoName(row.Memo, row.Sid);
        _serviceMappings.ResetItem(e.RowIndex);
    }

    private static string SuggestVideoName(string? serviceName, int sid)
    {
        var normalized = (serviceName ?? string.Empty).Trim();
        return normalized switch
        {
            "ＢＳテレ東" => "jk171",
            "ＢＳテレ東２" => "jk172",
            "ＢＳテレ東３" => "jk173",
            _ => $"jk{sid}"
        };
    }
}

internal sealed class ServiceSelectionForm : Form
{
    private readonly ListView _listView;
    private readonly List<ServiceCandidate> _candidates;

    public ServiceSelectionForm(IReadOnlyList<ServiceCandidate> candidates)
    {
        _candidates = candidates.ToList();
        Text = "EDCBサービス候補";
        Width = 720;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            CheckBoxes = true,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true
        };
        _listView.Columns.Add("サービス名", 280);
        _listView.Columns.Add("ONID", 90);
        _listView.Columns.Add("TSID", 90);
        _listView.Columns.Add("SID", 90);
        _listView.Columns.Add("既定video", 110);

        foreach (var candidate in _candidates)
        {
            var item = new ListViewItem(candidate.ServiceName);
            item.SubItems.Add(candidate.Onid.ToString());
            item.SubItems.Add(candidate.Tsid.ToString());
            item.SubItems.Add(candidate.Sid.ToString());
            item.SubItems.Add($"jk{candidate.Sid}");
            item.Tag = candidate;
            _listView.Items.Add(item);
        }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };

        var okButton = new Button { Text = "追加", AutoSize = true, DialogResult = DialogResult.OK };
        var cancelButton = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(okButton);
        buttons.Controls.Add(cancelButton);

        root.Controls.Add(_listView, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    public IReadOnlyList<ServiceCandidate> SelectedCandidates
        => _listView.CheckedItems
            .Cast<ListViewItem>()
            .Select(x => (ServiceCandidate)x.Tag!)
            .ToList();
}

internal sealed class ServiceMappingRow
{
    public bool Enabled { get; set; } = true;
    public string Video { get; set; } = string.Empty;
    public int Onid { get; set; }
    public int Tsid { get; set; }
    public int Sid { get; set; }
    public string Memo { get; set; } = string.Empty;

    public static ServiceMappingRow FromModel(ServiceMapping mapping)
        => new()
        {
            Enabled = mapping.Enabled,
            Video = mapping.Video,
            Onid = mapping.Onid,
            Tsid = mapping.Tsid,
            Sid = mapping.Sid,
            Memo = mapping.Memo
        };
}

internal sealed record ServiceCandidate(string ServiceName, ushort Onid, ushort Tsid, ushort Sid)
{
    public static ServiceCandidate FromService(EpgServiceInfo service)
        => new(service.service_name, service.ONID, service.TSID, service.SID);
}

internal sealed class LogViewerForm : Form
{
    private readonly AppLogger _logger;
    private readonly TextBox _textBox;

    public LogViewerForm(AppLogger logger)
    {
        _logger = logger;
        Text = "ログ - jkcnsl-edcb-epg-uploader";
        Icon = AppIconLoader.Load(new AppConfig(), Application.ExecutablePath);
        Width = 900;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new System.Drawing.Font("Consolas", 9F),
            WordWrap = false
        };

        Controls.Add(_textBox);
        _textBox.Text = string.Join(Environment.NewLine, _logger.GetSnapshot().Select(x => x.ToString()));

        _logger.EntryAdded += OnEntryAdded;
        FormClosed += (_, _) => _logger.EntryAdded -= OnEntryAdded;
    }

    private void OnEntryAdded(LogEntry entry)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnEntryAdded(entry));
            return;
        }

        if (_textBox.TextLength > 0)
        {
            _textBox.AppendText(Environment.NewLine);
        }
        _textBox.AppendText(entry.ToString());
    }
}

internal sealed class UploadScheduler : IDisposable
{
    private readonly ConfigStore _configStore;
    private readonly UploadWorker _worker;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private DateTimeOffset? _nextScheduledAt;
    private DateTimeOffset? _lastCompletedAt;
    private string? _lastMessage;
    private bool _isRunning;

    public event Action<SchedulerStatus>? StatusChanged;

    public UploadScheduler(ConfigStore configStore, UploadWorker worker, AppLogger logger)
    {
        _configStore = configStore;
        _worker = worker;
        _logger = logger;
    }

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopTask = Task.Run(async () =>
        {
            var firstRun = true;
            while (!_cts.IsCancellationRequested)
            {
                var config = _configStore.Current.Scheduler;
                if (firstRun && config.StartupDelaySeconds > 0)
                {
                    _nextScheduledAt = DateTimeOffset.Now.AddSeconds(config.StartupDelaySeconds);
                    PublishStatus("startup-delay");
                    await Task.Delay(TimeSpan.FromSeconds(config.StartupDelaySeconds), _cts.Token);
                }

                if (firstRun && config.RunImmediately)
                {
                    await RunOnceAsync("startup", _cts.Token);
                }

                firstRun = false;
                _nextScheduledAt = DateTimeOffset.Now.AddMinutes(Math.Max(1, config.IntervalMinutes));
                PublishStatus("scheduled");
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, config.IntervalMinutes)), _cts.Token);
                await RunOnceAsync("scheduled", _cts.Token);
            }
        }, _cts.Token);
    }

    public void TriggerNow(AppConfig? overrideConfig = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunOnceAsync("manual", _cts.Token, overrideConfig);
            }
            catch (OperationCanceledException)
            {
            }
        }, _cts.Token);
    }

    public void ReloadSchedule()
    {
        _logger.Info("スケジュール設定を再読み込みしました。次回周期から新設定を使用します。");
        PublishStatus("reloaded");
    }

    private async Task RunOnceAsync(string reason, CancellationToken cancellationToken, AppConfig? overrideConfig = null)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken))
        {
            _logger.Info($"送信要求をスキップしました。すでに送信中です。 reason={reason}");
            return;
        }

        try
        {
            _isRunning = true;
            PublishStatus(reason);
            var result = overrideConfig is null
                ? await _worker.ExecuteAsync(dryRun: false, singleChannel: null, cancellationToken)
                : await _worker.ExecuteAsync(overrideConfig, dryRun: false, singleChannel: null, cancellationToken);
            _lastCompletedAt = DateTimeOffset.Now;
            _lastMessage = result.Success ? "成功" : "失敗";
        }
        catch (Exception ex)
        {
            _lastCompletedAt = DateTimeOffset.Now;
            _lastMessage = "失敗";
            _logger.Error("定期送信に失敗しました。", ex);
        }
        finally
        {
            _isRunning = false;
            PublishStatus(reason);
            _runGate.Release();
        }
    }

    private void PublishStatus(string reason)
    {
        StatusChanged?.Invoke(new SchedulerStatus(_isRunning, reason, _nextScheduledAt, _lastCompletedAt, _lastMessage));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        _cts.Dispose();
        _runGate.Dispose();
    }
}

internal readonly record struct SchedulerStatus(
    bool IsRunning,
    string Reason,
    DateTimeOffset? NextScheduledAt,
    DateTimeOffset? LastCompletedAt,
    string? LastMessage);

internal sealed class UploadWorker
{
    private readonly ConfigStore _configStore;
    private readonly AppLogger _logger;

    public UploadWorker(ConfigStore configStore, AppLogger logger)
    {
        _configStore = configStore;
        _logger = logger;
    }

    public async Task<UploadResult> ExecuteAsync(bool dryRun, string? singleChannel, CancellationToken cancellationToken = default)
    {
        var config = _configStore.Current.DeepClone();
        return await ExecuteAsync(config, dryRun, singleChannel, cancellationToken);
    }

    public async Task<UploadResult> ExecuteAsync(AppConfig config, bool dryRun, string? singleChannel, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var hadError = false;
        var mappings = config.ServiceMappings
            .Where(x => x.Enabled)
            .Where(x => string.IsNullOrWhiteSpace(singleChannel) || string.Equals(x.Video, singleChannel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mappings.Count == 0)
        {
            var message = $"対象チャンネルが見つかりません: {singleChannel}";
            messages.Add(message);
            _logger.Info(message);
            return new UploadResult(false, messages);
        }

        _logger.Info($"送信開始 dryRun={dryRun} channels={string.Join(",", mappings.Select(x => x.Video))}");

        using var edcb = new EdcbEpgClient(config.Edcb);
        using var httpClient = CreateHttpClient(config.ImportApi);

        foreach (var mapping in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var programs = edcb.GetPrograms(mapping, config.Window);
                var line = $"{mapping.Video}: {programs.Count}件";
                messages.Add(line);
                _logger.Info(line);

                if (!dryRun)
                {
                    await UploadProgramsAsync(httpClient, config, mapping.Video, programs, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                hadError = true;
                var message = $"{mapping.Video}: 失敗 ({ex.Message})";
                messages.Add(message);
                _logger.Error($"{mapping.Video} の送信に失敗しました。", ex);
            }
        }

        messages.Add(dryRun
            ? (hadError ? "dry-run 一部失敗" : "dry-run 完了")
            : (hadError ? "送信一部失敗" : "送信完了"));
        _logger.Info(messages[^1]);
        return new UploadResult(!hadError, messages);
    }

    private async Task UploadProgramsAsync(
        HttpClient httpClient,
        AppConfig config,
        string channel,
        IReadOnlyList<ImportProgram> programs,
        CancellationToken cancellationToken)
    {
        var payload = new EpgImportRequest(
            channel,
            config.ImportApi.Source,
            DateTimeOffset.Now.ToString("O"),
            programs.Select(x => new EpgImportProgramRequest(
                x.Title,
                x.StartAt.ToString("O"),
                x.EndAt.ToString("O"),
                x.GenreCode,
                x.GenreName)).ToList());

        using var response = await httpClient.PostAsync(
            "api/admin/epg/import",
            JsonContent.Create(payload),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"API送信失敗 channel={channel} status={(int)response.StatusCode} body={body}");
        }
    }

    private static HttpClient CreateHttpClient(ImportApiConfig config)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds))
        };

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            client.DefaultRequestHeaders.Add("X-API-Key", config.ApiKey);
        }

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}

internal sealed class EdcbEpgClient : IDisposable
{
    private readonly CtrlCmdUtil _ctrl;

    public EdcbEpgClient(EdcbConfig config)
    {
        _ctrl = new CtrlCmdUtil();
        _ctrl.SetConnectTimeOut(config.ConnectTimeoutMilliseconds);
        _ctrl.SetSendMode(config.UseTcp);

        if (config.UseTcp)
        {
            _ctrl.SetNWSetting(IPAddress.Parse(config.Host), (uint)config.Port);
        }
        else
        {
            _ctrl.SetPipeSetting(config.EventName, config.PipeName);
        }
    }

    public IReadOnlyList<EpgServiceInfo> GetServices()
    {
        var services = new List<EpgServiceInfo>();
        var err = _ctrl.SendEnumService(ref services);
        EnsureSuccess(err, "サービス一覧の取得に失敗しました。");
        return services;
    }

    public IReadOnlyList<ImportProgram> GetPrograms(ServiceMapping mapping, WindowConfig window)
    {
        var events = new List<EpgEventInfo>();
        var err = _ctrl.SendEnumPgInfo(CreateServiceKey(mapping.Onid, mapping.Tsid, mapping.Sid), ref events);
        EnsureSuccess(err, $"{mapping.Video} の番組取得に失敗しました。");

        var from = DateTimeOffset.Now.AddHours(window.StartOffsetHours);
        var to = from.AddHours(window.DurationHours);
        var ordered = events
            .Where(x => x.StartTimeFlag != 0)
            .OrderBy(x => x.start_time)
            .ToList();

        var programs = new List<ImportProgram>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            var start = new DateTimeOffset(DateTime.SpecifyKind(item.start_time, DateTimeKind.Local));
            var end = ResolveEndAt(item, i + 1 < ordered.Count ? ordered[i + 1] : null);
            if (end is null || end <= start)
            {
                continue;
            }

            if (end < from || start > to)
            {
                continue;
            }

            var genre = item.ContentInfo?.nibbleList.FirstOrDefault();
            var title = string.IsNullOrWhiteSpace(item.ShortInfo?.event_name)
                ? "(番組名なし)"
                : item.ShortInfo!.event_name.Trim();
            programs.Add(new ImportProgram(
                title,
                start,
                end.Value,
                genre is null ? null : $"{genre.content_nibble_level_1:X1}{genre.content_nibble_level_2:X1}",
                null));
        }

        return programs;
    }

    public void Dispose()
    {
    }

    private static DateTimeOffset? ResolveEndAt(EpgEventInfo item, EpgEventInfo? next)
    {
        if (item.DurationFlag != 0 && item.durationSec > 0)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(item.start_time, DateTimeKind.Local)).AddSeconds(item.durationSec);
        }

        if (next is not null && next.StartTimeFlag != 0)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(next.start_time, DateTimeKind.Local));
        }

        return null;
    }

    private static ulong CreateServiceKey(ushort onid, ushort tsid, ushort sid)
        => ((ulong)onid << 32) | ((ulong)tsid << 16) | sid;

    private static void EnsureSuccess(ErrCode err, string message)
    {
        if (err != ErrCode.CMD_SUCCESS)
        {
            throw new InvalidOperationException($"{message} err={err}");
        }
    }
}

internal sealed class ConfigStore
{
    private readonly object _gate = new();
    private readonly AppPaths _paths;
    private AppConfig _current;

    public ConfigStore(AppPaths paths)
    {
        _paths = paths;
        _current = Load();
    }

    public AppConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current.DeepClone();
            }
        }
    }

    public void Update(AppConfig config, bool saveLocalSettings)
    {
        lock (_gate)
        {
            _current = config.DeepClone();
            if (saveLocalSettings)
            {
                SaveLocalSettings(_current);
            }
        }
    }

    private AppConfig Load()
    {
        var merged = new JsonObject();

        MergeFile(merged, _paths.AppSettingsPath);
        if (File.Exists(_paths.LocalAppSettingsPath))
        {
            MergeFile(merged, _paths.LocalAppSettingsPath);
        }

        return merged.Deserialize<AppConfig>(JsonDefaults.Options) ?? new AppConfig();
    }

    private void SaveLocalSettings(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.LocalAppSettingsPath)!);
        var root = new JsonObject
        {
            ["ImportApi"] = JsonSerializer.SerializeToNode(config.ImportApi, JsonDefaults.Options),
            ["Scheduler"] = JsonSerializer.SerializeToNode(config.Scheduler, JsonDefaults.Options),
            ["ServiceMappings"] = JsonSerializer.SerializeToNode(config.ServiceMappings, JsonDefaults.Options)
        };
        File.WriteAllText(_paths.LocalAppSettingsPath, root.ToJsonString(JsonDefaults.Options), new UTF8Encoding(false));
    }

    private static void MergeFile(JsonObject target, string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path));
        if (node is JsonObject source)
        {
            MergeObject(target, source);
        }
    }

    private static void MergeObject(JsonObject target, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Value is JsonObject sourceObject)
            {
                var targetObject = target[property.Key] as JsonObject ?? new JsonObject();
                target[property.Key] = targetObject;
                MergeObject(targetObject, sourceObject);
            }
            else
            {
                target[property.Key] = property.Value?.DeepClone();
            }
        }
    }
}

internal sealed class AppLogger
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly string _logDirectory;
    private const int MaxEntries = 500;

    public AppLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public event Action<LogEntry>? EntryAdded;

    public void Info(string message)
        => Add("INFO", message, null);

    public void Error(string message, Exception? exception)
        => Add("ERROR", message, exception);

    public IReadOnlyList<LogEntry> GetSnapshot()
    {
        lock (_gate)
        {
            return _entries.ToList();
        }
    }

    private void Add(string level, string message, Exception? exception)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, exception is null ? message : $"{message} {exception}");
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.Dequeue();
            }
        }

        var logPath = Path.Combine(_logDirectory, $"{DateTime.Now:yyyyMMdd}.log");
        File.AppendAllText(logPath, entry + Environment.NewLine, new UTF8Encoding(false));
        EntryAdded?.Invoke(entry);
    }
}

internal sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message)
{
    public override string ToString()
        => $"{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}";
}

internal static class AppPathResolver
{
    public static AppPaths Discover()
    {
        var exeDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(exeDir);
        while (current is not null)
        {
                var appSettingsPath = Path.Combine(current.FullName, "appsettings.json");
            if (File.Exists(appSettingsPath))
            {
                var localPath = Path.Combine(current.FullName, "local", "appsettings.json");
                var logDir = Path.Combine(current.FullName, "logs");
                var iconPath = Path.Combine(current.FullName, "Assets", "jkcnsl-edcb-epg-uploader.ico");
                return new AppPaths(appSettingsPath, localPath, logDir, iconPath, Path.Combine(exeDir, "jkcnsl-edcb-epg-uploader.exe"));
            }

            current = current.Parent;
        }

        throw new FileNotFoundException("appsettings.json が見つかりません。");
    }
}

internal sealed record AppPaths(string AppSettingsPath, string LocalAppSettingsPath, string LogDirectory, string IconPath, string ExecutablePath);

internal static class AppIconLoader
{
    public static Icon Load(AppConfig _, string executablePath)
    {
        try
        {
            var icon = Icon.ExtractAssociatedIcon(executablePath);
            if (icon is not null)
            {
                return icon;
            }
        }
        catch
        {
        }

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "jkcnsl-edcb-epg-uploader.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }
}

internal static class AutostartManager
{
    public static Task<string> InstallAsync(AppConfig config, string executablePath)
    {
        var shortcutPath = GetShortcutPath(config);
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell を取得できませんでした。");
        var shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("WScript.Shell を生成できませんでした。");
        var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])
            ?? throw new InvalidOperationException("ショートカットの作成に失敗しました。");

        var shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [executablePath]);
        shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, [""]);
        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(executablePath)!]);
        shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{executablePath},0"]);
        shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["jkcnsl-edcb-epg-uploader startup"]);
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

        return Task.FromResult($"スタートアップに登録しました: {shortcutPath}");
    }

    public static Task<string> UninstallAsync(AppConfig config)
    {
        var shortcutPath = GetShortcutPath(config);
        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
            return Task.FromResult($"スタートアップ登録を削除しました: {shortcutPath}");
        }

        return Task.FromResult($"スタートアップ登録は見つかりませんでした: {shortcutPath}");
    }

    private static string GetShortcutPath(AppConfig config)
    {
        var startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startupDirectory))
        {
            throw new InvalidOperationException("スタートアップフォルダを取得できませんでした。");
        }

        return Path.Combine(startupDirectory, "jkcnsl-edcb-epg-uploader.lnk");
    }
}

internal static class SingleInstanceGuard
{
    public static Mutex? TryAcquire(string mutexName)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (createdNew)
            {
                return mutex;
            }

            mutex.Dispose();
            return null;
        }
        catch (AbandonedMutexException)
        {
            return new Mutex(initiallyOwned: true, mutexName);
        }
    }
}

internal static class ConsoleWindow
{
    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    public static void Hide()
    {
        var handle = GetConsoleWindow();
        if (handle != 0)
        {
            ShowWindow(handle, 0);
        }
    }
}

internal static class ConsoleMode
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    public static void EnsureFor(CommandLineOptions option, AppConfig config)
    {
        if (!NeedsConsole(option, config))
        {
            return;
        }

        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }

        RebindStandardStreams();
    }

    private static bool NeedsConsole(CommandLineOptions option, AppConfig config)
    {
        if (option.ListServices || option.DryRun || option.InstallAutostart || option.UninstallAutostart || !string.IsNullOrWhiteSpace(option.Channel))
        {
            return true;
        }

        return option.Watch && !config.Scheduler.UseTrayIcon;
    }

    private static void RebindStandardStreams()
    {
        var stdout = Console.OpenStandardOutput();
        var stderr = Console.OpenStandardError();
        Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true });
    }
}

internal sealed class CommandLineOptions
{
    public bool DryRun { get; private init; }
    public bool ListServices { get; private init; }
    public bool Watch { get; private init; }
    public bool InstallAutostart { get; private init; }
    public bool UninstallAutostart { get; private init; }
    public string? Channel { get; private init; }

    public static CommandLineOptions Parse(string[] args)
    {
        string? channel = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--channel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                channel = args[i + 1];
                i++;
            }
        }

        var hasExplicitMode = args.Length > 0;

        return new CommandLineOptions
        {
            DryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase),
            ListServices = args.Contains("--list-services", StringComparer.OrdinalIgnoreCase),
            Watch = !hasExplicitMode || args.Contains("--watch", StringComparer.OrdinalIgnoreCase),
            InstallAutostart = args.Contains("--install-autostart", StringComparer.OrdinalIgnoreCase),
            UninstallAutostart = args.Contains("--uninstall-autostart", StringComparer.OrdinalIgnoreCase),
            Channel = channel
        };
    }
}

internal sealed record UploadResult(bool Success, IReadOnlyList<string> Messages);

internal sealed record ImportProgram(
    string Title,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    string? GenreCode,
    string? GenreName);

internal sealed record EpgImportRequest(
    string Channel,
    string? Source,
    string? CapturedAt,
    List<EpgImportProgramRequest> Programs);

internal sealed record EpgImportProgramRequest(
    string Title,
    string StartAt,
    string EndAt,
    string? GenreCode,
    string? GenreName);

internal sealed class AppConfig
{
    public EdcbConfig Edcb { get; set; } = new();
    public ImportApiConfig ImportApi { get; set; } = new();
    public WindowConfig Window { get; set; } = new();
    public SchedulerConfig Scheduler { get; set; } = new();
    public List<ServiceMapping> ServiceMappings { get; set; } = [];

    public AppConfig DeepClone()
        => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options) ?? new AppConfig();
}

internal sealed class EdcbConfig
{
    public bool UseTcp { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5678;
    public string EventName { get; set; } = "Global\\EpgTimerSrvConnect";
    public string PipeName { get; set; } = "EpgTimerSrvPipe";
    public int ConnectTimeoutMilliseconds { get; set; } = 15000;
    public string RootPath { get; set; } = "";
}

internal sealed class ImportApiConfig
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:5000/";
    public string ApiKey { get; set; } = "";
    public string Source { get; set; } = "airwave";
    public int TimeoutSeconds { get; set; } = 15;
}

internal sealed class WindowConfig
{
    public int StartOffsetHours { get; set; } = -6;
    public int DurationHours { get; set; } = 72;
}

internal sealed class SchedulerConfig
{
    public int IntervalMinutes { get; set; } = 15;
    public bool RunImmediately { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 15;
    public string MutexName { get; set; } = "Global\\jkcnsl-edcb-epg-uploader";
    public bool UseTrayIcon { get; set; } = true;
    public bool HideConsoleWindow { get; set; } = true;
}

internal sealed class ServiceMapping
{
    public bool Enabled { get; set; } = true;
    public string Video { get; set; } = "";
    public ushort Onid { get; set; }
    public ushort Tsid { get; set; }
    public ushort Sid { get; set; }
    public string Memo { get; set; } = "";
}
