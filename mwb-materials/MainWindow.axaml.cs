using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using mwb_materials.MwbMats;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace mwb_materials
{
    public partial class MainWindow : Window
    {
        private const string HelpUrl = "https://github.com/mushroom-guy/mwb-materials/blob/main/help.md";
        private const int MaxLogCharacters = 400_000;

        private static readonly string[] ClampSizes = new string[] { "4096", "2048", "1024", "512" };

        private readonly ConcurrentQueue<string> pendingLog = new ConcurrentQueue<string>();
        private readonly DispatcherTimer logTimer;
        private readonly StringBuilder logText = new StringBuilder();
        private AppSettings settings;
        private List<VmtPreset> vmtPresets = new List<VmtPreset>();
        private CancellationTokenSource batchCancellation;
        private bool bLoadingSettings;

        public MainWindow()
        {
            InitializeComponent();

            AlbedoCompression.ItemsSource = TextureExporter.Formats;
            NormalCompression.ItemsSource = TextureExporter.Formats;
            ExponentCompression.ItemsSource = TextureExporter.Formats;
            ClampComboBox.ItemsSource = ClampSizes;
            RdoComboBox.ItemsSource = TextureExporter.RdoLevels;

            logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (sender, args) => FlushLog());
            logTimer.Start();

            settings = AppSettings.Load();
            LoadVmtPresets(settings.VmtPreset);
            ApplySettings(settings);
            RegisterEvents();
        }

        private void RegisterEvents()
        {
            FolderButton.Click += FolderButton_Click;
            CancelButton.Click += (sender, args) => batchCancellation?.Cancel();
            HelpButton.Click += async (sender, args) => await Launcher.LaunchUriAsync(new Uri(HelpUrl));
            RestoreDefaultsButton.Click += (sender, args) => RestoreDefaultSettings();
            RefreshVmtPresetsButton.Click += (sender, args) => RefreshVmtPresets();
            ClearLogButton.Click += (sender, args) => ClearLog();
            BrowseDestinationButton.Click += async (sender, args) => await BrowseInto(VmtDestinationPath, "Output destination");
            BrowseEnvMapsButton.Click += async (sender, args) => await BrowseInto(EnvMapsDestination, "Envmaps folder");

            foreach (CheckBox check in new[] { AoCheck, OpenGlNormalCheck, InvertNormalBlueCheck, InvertOpacityCheck, KeepIntermediatesCheck,
                UseModelMaterialNamesCheck, PhongFromMwbMatsCheck, BatchMoveOutputCheck, BatchIncludeFoldersCheck, AlbedoMipMapsCheck, NormalMipMapsCheck, ExponentMipMapsCheck, CompressVtfsCheck })
            {
                check.IsCheckedChanged += (sender, args) => SaveSettings();
            }

            foreach (ComboBox combo in new[] { AlbedoCompression, NormalCompression, ExponentCompression, ClampComboBox, RdoComboBox, VmtPresetComboBox })
            {
                combo.SelectionChanged += (sender, args) => SaveSettings();
            }

            AoStrengthSlider.ValueChanged += (sender, args) =>
            {
                UpdateSliderLabels();
                SaveSettings();
            };

            foreach (Slider slider in new[] { AlphatestSlider, PhongMetalBoostSlider, PhongMetalMaxSlider, PhongGlossVariationSlider })
            {
                slider.ValueChanged += (sender, args) =>
                {
                    UpdateSliderLabels();
                    SaveSettings();
                };
            }

            ModeTabs.SelectionChanged += (sender, args) =>
            {
                UpdateModeLabels();
                SaveSettings();
            };

            ParallelMaterialsUpDown.ValueChanged += (sender, args) => SaveSettings();
            VmtDestinationPath.TextChanged += (sender, args) => SaveSettings();
            EnvMapsDestination.TextChanged += (sender, args) => SaveSettings();
            Closing += (sender, args) =>
            {
                batchCancellation?.Cancel();
                SaveSettings();
            };
        }

        #region Settings

        private void ApplySettings(AppSettings source)
        {
            bLoadingSettings = true;

            try
            {
                AoCheck.IsChecked = source.AoMasks;
                OpenGlNormalCheck.IsChecked = source.OpenGlNormal;
                InvertNormalBlueCheck.IsChecked = source.InvertNormalBlue;
                InvertOpacityCheck.IsChecked = source.InvertOpacity;
                KeepIntermediatesCheck.IsChecked = source.KeepIntermediates;
                UseModelMaterialNamesCheck.IsChecked = source.UseModelMaterialNames;
                BatchMoveOutputCheck.IsChecked = source.BatchMoveOutput;
                BatchIncludeFoldersCheck.IsChecked = source.BatchIncludeFolders;
                AlbedoMipMapsCheck.IsChecked = source.AlbedoMipMaps;
                NormalMipMapsCheck.IsChecked = source.NormalMipMaps;
                ExponentMipMapsCheck.IsChecked = source.ExponentMipMaps;
                CompressVtfsCheck.IsChecked = source.CompressVtfs;

                SetComboBoxValue(AlbedoCompression, TextureExporter.GetFormatName(source.AlbedoCompression ?? TextureExporter.FormatDXT5), TextureExporter.FormatDXT5);
                SetComboBoxValue(NormalCompression, TextureExporter.GetFormatName(source.NormalCompression ?? TextureExporter.FormatRGBA8888), TextureExporter.FormatRGBA8888);
                SetComboBoxValue(ExponentCompression, TextureExporter.GetFormatName(source.ExponentCompression ?? TextureExporter.FormatDXT5), TextureExporter.FormatDXT5);
                SetComboBoxValue(ClampComboBox, source.ClampSize, "4096");
                SetComboBoxValue(RdoComboBox, source.RdoLevel, TextureExporter.RdoOff);
                SetPresetComboBoxValue(source.VmtPreset);

                AoStrengthSlider.Value = Math.Clamp(source.AoAlbedoStrength, 0, 100);
                AlphatestSlider.Value = Math.Clamp(source.AlphatestReference, 0, 100);
                PhongMetalBoostSlider.Value = Math.Clamp(source.PhongMetalBoost, PhongMetalBoostSlider.Minimum, PhongMetalBoostSlider.Maximum);
                PhongMetalMaxSlider.Value = Math.Clamp(source.PhongMetalMax, PhongMetalMaxSlider.Minimum, PhongMetalMaxSlider.Maximum);
                PhongGlossVariationSlider.Value = Math.Clamp(source.PhongGlossVariation * 100, 0, 100);
                PhongFromMwbMatsCheck.IsChecked = source.PhongFromMwbMats;
                ModeTabs.SelectedItem = source.PhongMode ? PhongTab : PbrTab;
                UpdateModeLabels();
                ParallelMaterialsUpDown.Value = Math.Clamp(source.ParallelMaterials, 1, 16);
                UpdateSliderLabels();

                EnvMapsDestination.Text = source.EnvMapsFolder ?? string.Empty;
                VmtDestinationPath.Text = source.DestinationFolder ?? string.Empty;
            }
            finally
            {
                bLoadingSettings = false;
            }
        }

        private void RestoreDefaultSettings()
        {
            AppSettings defaults = new AppSettings()
            {
                PhongMode = IsPhongMode,
                DestinationFolder = VmtDestinationPath.Text,
                EnvMapsFolder = EnvMapsDestination.Text
            };

            ApplySettings(defaults);
            SaveSettings();
            AppendConsoleLine("Restored default settings.");
        }

        private void SaveSettings()
        {
            if (bLoadingSettings)
            {
                return;
            }

            settings.DestinationFolder = VmtDestinationPath.Text ?? string.Empty;
            settings.EnvMapsFolder = EnvMapsDestination.Text ?? string.Empty;
            settings.AoMasks = AoCheck.IsChecked == true;
            settings.OpenGlNormal = OpenGlNormalCheck.IsChecked == true;
            settings.InvertNormalBlue = InvertNormalBlueCheck.IsChecked == true;
            settings.InvertOpacity = InvertOpacityCheck.IsChecked == true;
            settings.KeepIntermediates = KeepIntermediatesCheck.IsChecked == true;
            settings.UseModelMaterialNames = UseModelMaterialNamesCheck.IsChecked == true;
            settings.PhongMode = IsPhongMode;
            settings.PhongMetalBoost = (float)PhongMetalBoostSlider.Value;
            settings.PhongMetalMax = (float)PhongMetalMaxSlider.Value;
            settings.PhongGlossVariation = (float)(PhongGlossVariationSlider.Value / 100.0);
            settings.PhongFromMwbMats = PhongFromMwbMatsCheck.IsChecked == true;
            settings.AoAlbedoStrength = (int)AoStrengthSlider.Value;
            settings.AlphatestReference = (int)AlphatestSlider.Value;
            settings.ClampSize = ClampComboBox.SelectedItem as string ?? "4096";
            settings.BatchMoveOutput = BatchMoveOutputCheck.IsChecked == true;
            settings.BatchIncludeFolders = BatchIncludeFoldersCheck.IsChecked == true;
            settings.AlbedoCompression = AlbedoCompression.SelectedItem as string ?? TextureExporter.FormatDXT5;
            settings.NormalCompression = NormalCompression.SelectedItem as string ?? TextureExporter.FormatRGBA8888;
            settings.ExponentCompression = ExponentCompression.SelectedItem as string ?? TextureExporter.FormatDXT5;
            settings.AlbedoMipMaps = AlbedoMipMapsCheck.IsChecked == true;
            settings.NormalMipMaps = NormalMipMapsCheck.IsChecked == true;
            settings.ExponentMipMaps = ExponentMipMapsCheck.IsChecked == true;
            settings.CompressVtfs = CompressVtfsCheck.IsChecked == true;
            settings.RdoLevel = RdoComboBox.SelectedItem as string ?? TextureExporter.RdoOff;
            settings.VmtPreset = GetSelectedVmtPreset().Id;
            settings.ParallelMaterials = (int)(ParallelMaterialsUpDown.Value ?? 1);
            settings.Save();
        }

        private static void SetComboBoxValue(ComboBox comboBox, string value, string fallback)
        {
            IEnumerable<string> items = comboBox.ItemsSource as IEnumerable<string> ?? Enumerable.Empty<string>();
            comboBox.SelectedItem = items.Contains(value) ? value : fallback;
        }

        private void UpdateSliderLabels()
        {
            AoStrengthValue.Text = (int)AoStrengthSlider.Value + "%";
            AlphatestValue.Text = (AlphatestSlider.Value / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            PhongMetalBoostValue.Text = PhongMetalBoostSlider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            PhongMetalMaxValue.Text = PhongMetalMaxSlider.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            PhongGlossVariationValue.Text = (int)PhongGlossVariationSlider.Value + "%";
        }

        private bool IsPhongMode => ModeTabs.SelectedItem == PhongTab;

        private void UpdateModeLabels()
        {
            if (IsBatchRunning)
            {
                return;
            }

            FolderButton.Content = IsPhongMode ? "Open materials folder…" : "Open folder…";
            SetStatus(IsPhongMode
                ? "Pick a materials folder (or a folder inside one) with phong VMTs to convert."
                : "Pick a material folder (or a folder of material folders) to convert.", null);
        }

        #endregion

        #region Presets

        private static IEnumerable<string> PresetDirectories()
        {
            yield return AppContext.BaseDirectory;
            yield return AppSettings.ConfigDirectory;
        }

        private void LoadVmtPresets(string preferredPresetId)
        {
            vmtPresets = VmtPresetLoader.LoadPresets(PresetDirectories(), AppendConsoleLine);
            VmtPresetComboBox.ItemsSource = vmtPresets;
            SetPresetComboBoxValue(preferredPresetId ?? string.Empty);
        }

        private void RefreshVmtPresets()
        {
            string presetId = GetSelectedVmtPreset().Id;
            bool wasLoadingSettings = bLoadingSettings;
            bLoadingSettings = true;

            try
            {
                LoadVmtPresets(presetId);
            }
            finally
            {
                bLoadingSettings = wasLoadingSettings;
            }

            SaveSettings();
            AppendConsoleLine("Reloaded VMT presets.");
        }

        private VmtPreset GetSelectedVmtPreset()
        {
            return VmtPresetComboBox.SelectedItem as VmtPreset ?? VmtPreset.Default;
        }

        private void SetPresetComboBoxValue(string presetId)
        {
            VmtPreset selected = string.IsNullOrEmpty(presetId)
                ? vmtPresets.FirstOrDefault(VmtPresetLoader.IsDefaultPreset)
                : vmtPresets.FirstOrDefault(preset => string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase));

            VmtPresetComboBox.SelectedItem = selected ?? vmtPresets.FirstOrDefault(VmtPresetLoader.IsDefaultPreset) ?? vmtPresets.FirstOrDefault();
        }

        #endregion

        #region Folder pickers

        private async Task<string> PickFolder(string title, string startPath)
        {
            FolderPickerOpenOptions options = new FolderPickerOpenOptions()
            {
                Title = title,
                AllowMultiple = false
            };

            if (!string.IsNullOrWhiteSpace(startPath) && System.IO.Directory.Exists(startPath))
            {
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startPath);
            }

            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(options);
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        private async Task BrowseInto(TextBox target, string title)
        {
            string path = await PickFolder(title, target.Text);

            if (!string.IsNullOrEmpty(path))
            {
                target.Text = path;
            }
        }

        #endregion

        #region Batch

        private async void FolderButton_Click(object sender, RoutedEventArgs e)
        {
            bool phongMode = IsPhongMode;
            string batchPath = await PickFolder(phongMode ? "Select a materials folder with phong VMTs" : "Select a material folder or a folder of material folders", settings.LastBatchFolder);

            if (string.IsNullOrEmpty(batchPath))
            {
                return;
            }

            settings.LastBatchFolder = batchPath;
            SaveSettings();

            MaterialManipulation.GenerateProperties props = new MaterialManipulation.GenerateProperties()
            {
                bAoMasks = AoCheck.IsChecked == true,
                bOpenGlNormal = OpenGlNormalCheck.IsChecked == true,
                bInvertNormalBlue = InvertNormalBlueCheck.IsChecked == true,
                bInvertOpacity = InvertOpacityCheck.IsChecked == true,
                bKeepIntermediates = KeepIntermediatesCheck.IsChecked == true,
                ClampSize = int.Parse(ClampComboBox.SelectedItem as string ?? "4096"),
                AoAlbedoStrength = (float)(AoStrengthSlider.Value / 100.0),
                LogFunc = AppendConsoleLine
            };

            BatchExporter.BatchProperties bProps = new BatchExporter.BatchProperties()
            {
                VmtRootPath = VmtDestinationPath.Text ?? string.Empty,
                EnvRootPath = EnvMapsDestination.Text ?? string.Empty,
                bMoveOutput = BatchMoveOutputCheck.IsChecked == true,
                bIncludeFolders = BatchIncludeFoldersCheck.IsChecked == true,
                AlbedoCompression = AlbedoCompression.SelectedItem as string,
                NormalCompression = NormalCompression.SelectedItem as string,
                ExponentCompression = ExponentCompression.SelectedItem as string,
                bAlbedoMipMaps = AlbedoMipMapsCheck.IsChecked == true,
                bNormalMipMaps = NormalMipMapsCheck.IsChecked == true,
                bExponentMipMaps = ExponentMipMapsCheck.IsChecked == true,
                bCompressVtfs = CompressVtfsCheck.IsChecked == true,
                RdoLevel = RdoComboBox.SelectedItem as string ?? TextureExporter.RdoOff,
                bKeepIntermediates = KeepIntermediatesCheck.IsChecked == true,
                bUseModelMaterialNames = !phongMode && UseModelMaterialNamesCheck.IsChecked == true,
                bConvertPhongMaterials = phongMode,
                PhongSettings = new PhongConversionSettings()
                {
                    MetalBoost = (float)PhongMetalBoostSlider.Value,
                    MetalMax = (float)PhongMetalMaxSlider.Value,
                    GlossMaskInfluence = (float)(PhongGlossVariationSlider.Value / 100.0),
                    FromMwbMats = PhongFromMwbMatsCheck.IsChecked == true
                },
                VmtPreset = GetSelectedVmtPreset(),
                AlphatestReference = (float)(AlphatestSlider.Value / 100.0),
                MaxParallelJobs = (int)(ParallelMaterialsUpDown.Value ?? 1),
                GenerateProps = props,
                LogFunc = AppendConsoleLine
            };

            ClearLog();
            AppendConsoleLine("Starting batch: " + batchPath);
            SetBatchRunning(true);
            SetStatus("Scanning folders…", null);

            Progress<BatchExporter.BatchProgress> progress = new Progress<BatchExporter.BatchProgress>(UpdateProgress);
            Stopwatch timer = Stopwatch.StartNew();
            batchCancellation = new CancellationTokenSource();

            try
            {
                int materials = await Task.Run(() => BatchExporter.StartBatch(batchPath, bProps, progress, batchCancellation.Token));
                timer.Stop();
                FlushLog();

                string elapsed = timer.Elapsed.ToString(@"m\:ss\.fff");

                if (materials == 0)
                {
                    SetStatus((phongMode ? "No phong materials found in " : "No source textures found in ") + batchPath, "ErrorBrush");
                }
                else
                {
                    AppendConsoleLine("Batch complete.");
                    SetStatus("Generated " + materials + " material" + (materials == 1 ? "" : "s") + " in " + elapsed, "SuccessBrush");
                    BatchProgress.Value = BatchProgress.Maximum;
                }
            }
            catch (OperationCanceledException)
            {
                AppendConsoleLine("Batch cancelled.");
                SetStatus("Cancelled.", "ErrorBrush");
            }
            catch (Exception ex)
            {
                AppendConsoleLine("Batch failed: " + ex.Message);
                SetStatus("Batch failed: " + ex.Message, "ErrorBrush");
                await MessageDialog.Show(this, "Batch export failed", ex.Message);
            }
            finally
            {
                batchCancellation.Dispose();
                batchCancellation = null;
                SetBatchRunning(false);
            }
        }

        private void UpdateProgress(BatchExporter.BatchProgress progress)
        {
            BatchProgress.Maximum = Math.Max(1, progress.Total);
            BatchProgress.Value = progress.Completed;

            string status = progress.Completed + " / " + progress.Total + " materials";

            if (!string.IsNullOrEmpty(progress.Current))
            {
                status += "  ·  " + progress.Current;
            }

            SetStatus(status, null);
        }

        private bool IsBatchRunning => batchCancellation != null;

        private void SetBatchRunning(bool running)
        {
            SettingsPanel.IsEnabled = !running;
            FolderButton.IsEnabled = !running;
            RestoreDefaultsButton.IsEnabled = !running;
            CancelButton.IsVisible = running;
            BatchProgress.IsIndeterminate = false;

            if (running)
            {
                BatchProgress.Value = 0;
            }
        }

        private void SetStatus(string text, string brushKey)
        {
            StatusText.Text = text;

            if (brushKey != null && this.TryFindResource(brushKey, out object brush) && brush is IBrush statusBrush)
            {
                StatusText.Foreground = statusBrush;
            }
            else if (this.TryFindResource("MutedBrush", out object muted) && muted is IBrush mutedBrush)
            {
                StatusText.Foreground = mutedBrush;
            }
        }

        #endregion

        #region Log

        private void AppendConsoleLine(string message)
        {
            pendingLog.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
        }

        private void FlushLog()
        {
            if (pendingLog.IsEmpty)
            {
                return;
            }

            while (pendingLog.TryDequeue(out string line))
            {
                logText.Append(line).Append('\n');
            }

            if (logText.Length > MaxLogCharacters)
            {
                int cut = logText.ToString().IndexOf('\n', logText.Length - MaxLogCharacters / 2);
                logText.Remove(0, cut + 1);
            }

            ConsoleTextBox.Text = logText.ToString();
            ConsoleTextBox.CaretIndex = ConsoleTextBox.Text.Length;
        }

        private void ClearLog()
        {
            while (pendingLog.TryDequeue(out _))
            {
            }

            logText.Clear();
            ConsoleTextBox.Text = string.Empty;
        }

        #endregion
    }
}
