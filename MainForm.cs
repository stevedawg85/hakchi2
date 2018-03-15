﻿using com.clusterrr.clovershell;
using com.clusterrr.hakchi_gui.Properties;
using AutoUpdaterDotNET;
using SevenZip;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Net;

namespace com.clusterrr.hakchi_gui
{
    public partial class MainForm : Form
    {
        /// <summary>
        /// The URL for the update metadata XML file
        /// </summary>
        private static string UPDATE_XML_URL = "https://teamshinkansen.github.io/xml/updates/update.xml";

        public enum OriginalGamesPosition { AtTop = 0, AtBottom = 1, Sorted = 2, Hidden = 3 }
        public enum ConsoleType { NES = 0, Famicom = 1, SNES = 2, SuperFamicom = 3, Unknown = 255 }
        public long DefaultMaxGamesSize
        {
            get
            {
                switch (ConfigIni.Instance.ConsoleType)
                {
                    default:
                    case ConsoleType.NES:
                    case ConsoleType.Famicom:
                        return 300;
                    case ConsoleType.SNES:
                    case ConsoleType.SuperFamicom:
                        return 200;
                }
            }
        }
        public string GetConsoleTypeName()
        {
            return GetConsoleTypeName(hakchi.DetectedConsoleType);
        }
        public string GetConsoleTypeName(ConsoleType? c)
        {
            switch (c)
            {
                case ConsoleType.NES: return "NES";
                case ConsoleType.Famicom: return "Famicom";
                case ConsoleType.SNES: return "SNES";
                case ConsoleType.SuperFamicom: return "Super Famicom";
                case ConsoleType.Unknown: return "Unknown";
            }
            return string.Empty;
        }

        public static IEnumerable<string> InternalMods;
        public static bool? DownloadCover;
        public const int MaxGamesPerFolder = 50;

        mooftpserv.Server ftpServer;

        private class GamesSorter : IComparer
        {
            public int Compare(object o1, object o2)
            {
                if (o1 is ListViewItem)
                    o1 = (o1 as ListViewItem).Tag;
                if (o2 is ListViewItem)
                    o2 = (o2 as ListViewItem).Tag;
                if (!(o1 is NesApplication))
                    return -1;
                if (!(o2 is NesApplication))
                    return 1;
                return ((o1 as NesApplication).SortName.CompareTo((o2 as NesApplication).SortName));
            }
        }

        public MainForm()
        {
            InitializeComponent();
            FormInitialize();
            hakchi.Initialize();
            hakchi.Shell.OnConnected += Shell_OnConnected;
            hakchi.Shell.OnDisconnected += Shell_OnDisconnected;

            ftpServer = new mooftpserv.Server();
            ftpServer.AuthHandler = new mooftpserv.NesMiniAuthHandler();
            ftpServer.FileSystemHandler = new mooftpserv.NesMiniFileSystemHandler(hakchi.Shell);
            ftpServer.LogHandler = new mooftpserv.DebugLogHandler();
            ftpServer.LocalPort = 1021;

            if (ConfigIni.Instance.FtpServer)
                FTPToolStripMenuItem_Click(null, null);
            if (ConfigIni.Instance.TelnetServer)
                shellToolStripMenuItem_Click(null, null);
        }

        void SetWindowTitle()
        {
            string title = $"hakchi2 CE v{Shared.AppDisplayVersion}";

#if DEBUG
            title += " (Debug";
#if VERY_DEBUG
            title += ", very verbose mode";
#endif
            title += ")"
#endif
            ;

            if (hakchi.DetectedConsoleType != null)
            {
                title += " - " + GetConsoleTypeName(hakchi.DetectedConsoleType);
                if (hakchi.CustomFirmwareLoaded) title += " (HSQS)";
            }

            this.Text = title;
        }

        void FormInitialize()
        {
            try
            {
                // icon workaround
                Icon = Resources.icon;

                // prepare collections
                InternalMods = from m in Directory.GetFiles(Path.Combine(Program.BaseDirectoryInternal, "mods/hmods")) select Path.GetFileNameWithoutExtension(m);
                LoadLanguages();
                CoreCollection.Load();

                // init list view control
                listViewGames.ListViewItemSorter = new GamesSorter();
                listViewGames.DoubleBuffered(true);

                // fill games collections combo box
                foreach (ConsoleType c in Enum.GetValues(typeof(ConsoleType)))
                    if (c != ConsoleType.Unknown)
                        gamesConsoleComboBox.Items.Add(GetConsoleTypeName(c));

                // prepare controls
                SyncConsoleType();

                // Little tweak for easy translation
                var tbl = textBoxName.Left;
                textBoxName.Left = labelName.Left + labelName.Width;
                textBoxName.Width -= textBoxName.Left - tbl;
                maskedTextBoxReleaseDate.Left = label1.Left + label1.Width + 3;
                tbl = textBoxPublisher.Left;
                textBoxPublisher.Left = label2.Left + label2.Width;
                textBoxPublisher.Width -= textBoxPublisher.Left - tbl;

                // Tweeks for message boxes
                MessageBoxManager.Yes = MessageBoxManager.Retry = Resources.Yes;
                MessageBoxManager.No = MessageBoxManager.Ignore = Resources.No;
                MessageBoxManager.Cancel = Resources.NoForAll;
                MessageBoxManager.Abort = Resources.YesForAll;

                // supported extensions in add games dialog
                string extensions = string.Empty;
                extensions += "All Files|*.*|Archive Files|*.zip;*.7z;*.rar|";
                foreach(var system in CoreCollection.Systems)
                {
                    extensions += system + "|*" + string.Join(";*", CoreCollection.GetExtensionsFromSystem(system).ToArray()) + "|";
                }
                openFileDialogNes.Filter = extensions.Trim('|');
                
                // Loading games database in background
                new Thread(NesGame.LoadCache).Start();
                new Thread(SnesGame.LoadCache).Start();
                // Recalculate games in background
                new Thread(RecalculateSelectedGamesThread).Start();

                // servers menu settings
                openFTPInExplorerToolStripMenuItem.Enabled = FTPToolStripMenuItem.Checked = ConfigIni.Instance.FtpServer;
                openTelnetToolStripMenuItem.Enabled = shellToolStripMenuItem.Checked = ConfigIni.Instance.TelnetServer;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, "Critical error: " + ex.Message + ex.StackTrace, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private DialogResult BackgroundThreadMessageBox(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return (DialogResult)this.Invoke(new Func<DialogResult>(
                                   () => { return MessageBox.Show(this, text, title, buttons, icon); }));
        }

        void Shell_OnConnected()
        {
            try
            {
                if (hakchi.MinimalMemboot)
                    return;

                if (hakchi.CanInteract)
                {
                    if (hakchi.DetectedConsoleType != null)
                    {
                        if (hakchi.DetectedConsoleType != ConsoleType.Unknown)
                            ConfigIni.Instance.ConsoleType = (ConsoleType)hakchi.DetectedConsoleType;
                        ConfigIni.Instance.LastConnectedConsoleType = (ConsoleType)hakchi.DetectedConsoleType;
                    }
                    Invoke(new Action(SyncConsoleType));

                    if (hakchi.SystemEligibleForRootfsUpdate())
                    {
                        if (BackgroundThreadMessageBox(Resources.SystemEligibleForRootfsUpdate, Resources.OutdatedScripts, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        {
                            if (MembootCustomKernel())
                            {
                                BackgroundThreadMessageBox(Resources.DoneYouCanUpload, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            return;
                        }
                    }

                    Invoke(new Action(UpdateLocalCache));
                    WorkerForm.GetMemoryStats();
                    new Thread(RecalculateSelectedGamesThread).Start();
                }
                else
                {
                    if (hakchi.SystemRequiresReflash())
                    {
                        if (BackgroundThreadMessageBox(Resources.SystemRequiresReflash, Resources.OutdatedKernel, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        {
                            if (FlashCustomKernel())
                            {
                                BackgroundThreadMessageBox(Resources.DoneYouCanUpload, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            return;
                        }
                    }
                    else if (hakchi.SystemRequiresRootfsUpdate())
                    {
                        if (BackgroundThreadMessageBox(Resources.SystemRequiresRootfsUpdate, Resources.OutdatedScripts, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        {
                            if (MembootCustomKernel())
                            {
                                BackgroundThreadMessageBox(Resources.DoneYouCanUpload, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            return;
                        }
                    }

                    // show warning message that any interaction is ill-advised
                    BackgroundThreadMessageBox(Resources.PleaseUpdate, Resources.Warning, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
            }
        }

        void Shell_OnDisconnected()
        {
            Invoke(new Action(SyncConsoleType));
        }

        static ConsoleType lastConsoleType = ConsoleType.Unknown;
        public void SyncConsoleType()
        {
            // update window title
            SetWindowTitle();

            // skip if unchanged
            if (ConfigIni.Instance.ConsoleType == lastConsoleType)
                return;

            // select games collection
            for (int i = 0; i < gamesConsoleComboBox.Items.Count; ++i)
            {
                if (GetConsoleTypeName(ConfigIni.Instance.ConsoleType) == gamesConsoleComboBox.Items[i] as string)
                {
                    gamesConsoleComboBox.SelectedIndex = i;
                    break;
                }
            }

            // console settings
            enableUSBHostToolStripMenuItem.Checked = ConfigIni.Instance.UsbHost;
            useExtendedFontToolStripMenuItem.Checked = ConfigIni.Instance.UseFont;
            epilepsyProtectionToolStripMenuItem.Checked = ConfigIni.Instance.AntiArmetLevel > 0;
            selectButtonCombinationToolStripMenuItem.Enabled = resetUsingCombinationOfButtonsToolStripMenuItem.Checked = ConfigIni.Instance.ResetHack;
            enableAutofireToolStripMenuItem.Checked = ConfigIni.Instance.AutofireHack;
            useXYOnClassicControllerAsAutofireABToolStripMenuItem.Checked = ConfigIni.Instance.AutofireXYHack;
            upABStartOnSecondControllerToolStripMenuItem.Enabled = true; // hakchi.DetectedConsoleType == ConsoleType.Famicom;
            upABStartOnSecondControllerToolStripMenuItem.Checked = ConfigIni.Instance.FcStart && upABStartOnSecondControllerToolStripMenuItem.Enabled;

            // more settings
            compressGamesToolStripMenuItem.Checked = ConfigIni.Instance.Compress;
            compressBoxArtToolStripMenuItem.Checked = ConfigIni.Instance.CompressCover;
            centerBoxArtThumbnailToolStripMenuItem.Checked = ConfigIni.Instance.CenterThumbnail;
            separateGamesForMultibootToolStripMenuItem.Checked = ConfigIni.Instance.SeparateGameStorage;
            disableHakchi2PopupsToolStripMenuItem.Checked = ConfigIni.Instance.DisablePopups;
            useLinkedSyncToolStripMenuItem.Checked = ConfigIni.Instance.SyncLinked;

            // sfrom tool
            enableSFROMToolToolStripMenuItem.Checked = ConfigIni.Instance.UseSFROMTool;
            usePCMPatchWhenAvailableToolStripMenuItem.Checked = ConfigIni.Instance.UsePCMPatch;
            if (SfromToolWrapper.IsInstalled)
            {
                usePCMPatchWhenAvailableToolStripMenuItem.Enabled = enableSFROMToolToolStripMenuItem.Checked;
            }
            else
            {
                ConfigIni.Instance.UseSFROMTool = enableSFROMToolToolStripMenuItem.Checked = false;
                usePCMPatchWhenAvailableToolStripMenuItem.Enabled = false;
            }

            // initial view menu
            positionAtTheTopToolStripMenuItem.Checked = ConfigIni.Instance.OriginalGamesPosition == OriginalGamesPosition.AtTop;
            positionAtTheBottomToolStripMenuItem.Checked = ConfigIni.Instance.OriginalGamesPosition == OriginalGamesPosition.AtBottom;
            positionSortedToolStripMenuItem.Checked = ConfigIni.Instance.OriginalGamesPosition == OriginalGamesPosition.Sorted;
            positionHiddenToolStripMenuItem.Checked = ConfigIni.Instance.OriginalGamesPosition == OriginalGamesPosition.Hidden;
            groupByAppTypeToolStripMenuItem.Checked = ConfigIni.Instance.GroupGamesByAppType;

            // folders modes
            disablePagefoldersToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 0;
            automaticToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 2;
            automaticOriginalToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 3;
            pagesToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 4;
            pagesOriginalToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 5;
            foldersToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 6;
            foldersOriginalToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 7;
            foldersSplitByFirstLetterToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 8;
            foldersSplitByFirstLetterOriginalToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 9;
            customToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 99;

            // items per folder
            maximumGamesPerFolderToolStripMenuItem.DropDownItems.Clear();
            for (byte f = 20; f <= 100; f += ((f < 50) ? (byte)5 : (byte)10))
            {
                var item = new ToolStripMenuItem();
                item.Name = "folders" + f.ToString();
                item.Text = f.ToString();
                item.Tag = f;
                if (f >= MaxGamesPerFolder)
                    item.Text += $" ({Resources.NotRecommended})";
                item.Checked = ConfigIni.Instance.MaxGamesPerFolder == f;
                item.Click += delegate (object sender, EventArgs e)
                {
                    var old = maximumGamesPerFolderToolStripMenuItem.DropDownItems.Find("folders" + ConfigIni.Instance.MaxGamesPerFolder.ToString(), true);
                    if (old.Count() > 0)
                        (old.First() as ToolStripMenuItem).Checked = false;
                    ConfigIni.Instance.MaxGamesPerFolder = (byte)((sender as ToolStripMenuItem).Tag);
                    var n = maximumGamesPerFolderToolStripMenuItem.DropDownItems.Find("folders" + ConfigIni.Instance.MaxGamesPerFolder.ToString(), true);
                    if (n.Count() > 0)
                        (n.First() as ToolStripMenuItem).Checked = true;
                };
                maximumGamesPerFolderToolStripMenuItem.DropDownItems.Add(item);
            }

            LoadPresets();
            LoadGames();
            lastConsoleType = ConfigIni.Instance.ConsoleType;
        }

        void UpdateLocalCache()
        {
            string cachePath = Path.Combine(Program.BaseDirectoryExternal, "games_cache");
            var games = new NesMenuCollection();
            foreach (NesDefaultGame game in NesApplication.DefaultGames)
            {
                if (!Directory.Exists(Path.Combine(cachePath, game.Code)))
                    games.Add(game);
            }

            if (games.Count > 0)
            {
                var workerForm = new WorkerForm(this);
                workerForm.Text = Resources.UpdatingLocalCache;
                workerForm.Task = WorkerForm.Tasks.UpdateLocalCache;
                workerForm.Games = games;

                if (workerForm.Start() == DialogResult.OK)
                    Debug.WriteLine("successfully updated local original games cache.");
            }
            else
                Debug.WriteLine("local original games cache in sync.");
        }

        ListViewGroup[] lgvGroups = null;
        SortedDictionary<string, ListViewGroup> lgvAppGroups = null;
        SortedDictionary<string, ListViewGroup> lgvCustomGroups = null;
        public void LoadGames()
        {
            Debug.WriteLine("Loading games");
            var selected = ConfigIni.Instance.SelectedGames;

            // list original game directories
            var originalGameDirs = new List<string>();
            foreach(var defaultGame in NesApplication.DefaultGames)
            {
                string gameDir = Path.Combine(NesApplication.OriginalGamesDirectory, defaultGame.Code);
                if (Directory.Exists(gameDir))
                    originalGameDirs.Add(gameDir);
            }

            // add custom games
            Directory.CreateDirectory(NesApplication.GamesDirectory);
            var gameDirs = Shared.ConcatArrays(Directory.GetDirectories(NesApplication.GamesDirectory), originalGameDirs.ToArray());
            var games = new List<NesApplication>();
            foreach (var gameDir in gameDirs)
            {
                try
                {
                    // Removing empty directories without errors
                    try
                    {
                        var game = NesApplication.FromDirectory(gameDir);
                        games.Add(game);
                    }
                    catch (FileNotFoundException ex) // Remove bad directories if any
                    {
                        Debug.WriteLine(ex.Message + ex.StackTrace);
                        Directory.Delete(gameDir, true);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message + ex.StackTrace);
                    MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    break;
                }
            }

            // create groups
            if (lgvGroups == null)
            {
                // standard groups
                lgvGroups = new ListViewGroup[5];
                lgvGroups[0] = new ListViewGroup(Resources.ListCategoryNew, HorizontalAlignment.Center);
                lgvGroups[1] = new ListViewGroup(Resources.ListCategoryOriginal, HorizontalAlignment.Center);
                lgvGroups[2] = new ListViewGroup(Resources.ListCategoryCustom, HorizontalAlignment.Center);
                lgvGroups[3] = new ListViewGroup(Resources.ListCategoryAll, HorizontalAlignment.Center);
                lgvGroups[4] = new ListViewGroup(Resources.ListCategoryUnknown, HorizontalAlignment.Center);

                // order by app groups
                lgvAppGroups = new SortedDictionary<string, ListViewGroup>();
                foreach (var system in CoreCollection.Systems)
                {
                    lgvAppGroups[system] = new ListViewGroup(system, HorizontalAlignment.Center);
                }
                foreach (var appInfo in AppTypeCollection.Apps)
                {
                    if (!lgvAppGroups.ContainsKey(appInfo.Name))
                    {
                        lgvAppGroups[appInfo.Name] = new ListViewGroup(appInfo.Name, HorizontalAlignment.Center);
                    }
                }

                // custom generated on the fly groups
                lgvCustomGroups = new SortedDictionary<string, ListViewGroup>();
            }

            listViewGames.BeginUpdate();
            listViewGames.Groups.Clear();
            listViewGames.Items.Clear();

            // add games to ListView control
            var gamesSorted = games.OrderBy(o => o.SortName);
            foreach (var game in gamesSorted)
            {
                if (ConfigIni.Instance.OriginalGamesPosition == OriginalGamesPosition.Hidden && game.IsOriginalGame)
                    continue;

                var listViewItem = new ListViewItem(game.Name);
                listViewItem.Tag = game;
                listViewItem.Checked = selected.Contains(game.Code) || ConfigIni.Instance.HiddenGames.Contains(game.Code);

                ListViewGroup group = null;
                if (game.IsOriginalGame)
                {
                    if (ConfigIni.Instance.OriginalGamesPosition != OriginalGamesPosition.Sorted)
                        group = lgvGroups[1];
                }

                if(group == null)
                {
                    if (ConfigIni.Instance.GroupGamesByAppType)
                    {
                        var appinfo = game.Metadata.AppInfo;
                        if (!appinfo.Unknown)
                        {
                            group = lgvAppGroups[appinfo.Name];
                        }
                        else if (!string.IsNullOrEmpty(game.Metadata.System) && lgvAppGroups.ContainsKey(game.Metadata.System))
                        {
                            group = lgvAppGroups[game.Metadata.System];
                        }
                        else
                        {
                            if (game.Desktop.Bin.Trim().Length == 0)
                            {
                                group = lgvGroups[4];
                            }
                            else
                            {
                                string app = game.Desktop.Bin.Trim();
                                if (!lgvCustomGroups.ContainsKey(app))
                                    lgvCustomGroups.Add(app, new ListViewGroup(app, HorizontalAlignment.Center));
                                group = lgvCustomGroups[app];
                            }
                        }
                    }
                    else
                    {
                        group = game.IsOriginalGame ? lgvGroups[1] : lgvGroups[2];
                    }
                }

                listViewItem.Group = group;
                listViewGames.Items.Add(listViewItem);
            }

            // add groups in the right order
            if (ConfigIni.Instance.OriginalGamesPosition == OriginalGamesPosition.AtTop)
            {
                listViewGames.Groups.Add(lgvGroups[0]);
                listViewGames.Groups.Add(lgvGroups[1]);
                if (ConfigIni.Instance.GroupGamesByAppType)
                {
                    foreach (var group in lgvAppGroups) listViewGames.Groups.Add(group.Value);
                    foreach (var group in lgvCustomGroups) listViewGames.Groups.Add(group.Value);
                }
                else
                    listViewGames.Groups.Add(lgvGroups[2]);
                listViewGames.Groups.Add(lgvGroups[4]);
            }
            else if (ConfigIni.Instance.OriginalGamesPosition == OriginalGamesPosition.AtBottom)
            {
                listViewGames.Groups.Add(lgvGroups[0]);
                if (ConfigIni.Instance.GroupGamesByAppType)
                {
                    foreach (var group in lgvAppGroups) listViewGames.Groups.Add(group.Value);
                    foreach (var group in lgvCustomGroups) listViewGames.Groups.Add(group.Value);
                }
                else
                    listViewGames.Groups.Add(lgvGroups[2]);
                listViewGames.Groups.Add(lgvGroups[4]);
                listViewGames.Groups.Add(lgvGroups[1]);
            }
            else if (ConfigIni.Instance.GroupGamesByAppType)
            {
                listViewGames.Groups.Add(lgvGroups[0]);
                foreach (var group in lgvAppGroups) listViewGames.Groups.Add(group.Value);
                foreach (var group in lgvCustomGroups) listViewGames.Groups.Add(group.Value);
                listViewGames.Groups.Add(lgvGroups[4]);
            }

            // done!
            listViewGames.EndUpdate();

            // update counters
            RecalculateSelectedGames();
            ShowSelected();
        }

        private bool showingSelected = false;
        public void ShowSelected()
        {
            object selected = null;
            var selectedAll = listViewGames.SelectedItems;
            if (selectedAll.Count == 1) selected = selectedAll[0].Tag;

            showingSelected = true;
            if (selected == null)
            {
                groupBoxOptions.Visible = true;
                groupBoxOptions.Enabled = false;
                labelID.Text = "ID: ";
                labelSize.Text = Resources.Size;
                textBoxName.Text = "";
                textBoxSortName.Text = "";
                radioButtonOne.Checked = true;
                radioButtonTwo.Checked = false;
                radioButtonTwoSim.Checked = false;
                maskedTextBoxReleaseDate.Text = "";
                textBoxPublisher.Text = "";
                textBoxArguments.Text = "";
                numericUpDownSaveCount.Value = 0;
                pictureBoxArt.Image = Resources.noboxart;
                pictureBoxThumbnail.Image = null;
                pictureBoxThumbnail.Visible = false;
                buttonShowGameGenieDatabase.Enabled = textBoxGameGenie.Enabled = false;
                textBoxGameGenie.Text = "";
                checkBoxCompressed.Enabled = false;
                checkBoxCompressed.Checked = false;
            }
            else
            {
                var app = selected as NesApplication;
                groupBoxOptions.Visible = true;
                labelID.Text = "ID: " + app.Code;
                labelSize.Text = $"{Resources.Size} {Shared.SizeSuffix(app.Size())}";
                textBoxName.Text = app.Name;
                textBoxSortName.Text = app.SortName;
                if (app.Desktop.Simultaneous && app.Desktop.Players == 2)
                    radioButtonTwoSim.Checked = true;
                else if (app.Desktop.Players == 2)
                    radioButtonTwo.Checked = true;
                else
                    radioButtonOne.Checked = true;
                maskedTextBoxReleaseDate.Text = app.Desktop.ReleaseDate;
                textBoxPublisher.Text = app.Desktop.Publisher;
                textBoxArguments.Text = app.Desktop.Exec;
                numericUpDownSaveCount.Value = app.Desktop.SaveCount;
                pictureBoxArt.Image = app.Image;
                pictureBoxThumbnail.Image = app.Thumbnail;
                pictureBoxThumbnail.Visible = true;
                buttonShowGameGenieDatabase.Enabled = app is NesGame; //ISupportsGameGenie;
                textBoxGameGenie.Enabled = app is ISupportsGameGenie;
                textBoxGameGenie.Text = (app is ISupportsGameGenie) ? (app as NesApplication).GameGenie : "";
                groupBoxOptions.Enabled = true;
                if (app.CompressPossible().Count() > 0)
                {
                    checkBoxCompressed.Enabled = true;
                    checkBoxCompressed.Checked = false;
                }
                else if (app.DecompressPossible().Count() > 0)
                {
                    checkBoxCompressed.Enabled = true;
                    checkBoxCompressed.Checked = true;
                }
                else
                {
                    checkBoxCompressed.Enabled = false;
                    checkBoxCompressed.Checked = false;
                }
            }
            showingSelected = false;
        }

        void LoadPresets()
        {
            while (presetsToolStripMenuItem.DropDownItems.Count > 3)
                presetsToolStripMenuItem.DropDownItems.RemoveAt(0);
            deletePresetToolStripMenuItem.Enabled = false;
            deletePresetToolStripMenuItem.DropDownItems.Clear();
            int i = 0;
            foreach (var preset in ConfigIni.Instance.Presets.Keys.OrderBy(o => o))
            {
                presetsToolStripMenuItem.DropDownItems.Insert(i, new ToolStripMenuItem(preset, null,
                    delegate (object sender, EventArgs e)
                    {
                        var presetSelected = ConfigIni.Instance.Presets[preset];
                        var selected = ConfigIni.Instance.SelectedGames;
                        selected.Clear();
                        for (int j = 1; j < listViewGames.Items.Count; j++)
                        {
                            var code = (listViewGames.Items[j].Tag as NesApplication).Code;
                            if (presetSelected.Contains(code))
                            {
                                listViewGames.Items[j].Checked = true;
                                selected.Add(code);
                            }
                            else
                            {
                                listViewGames.Items[j].Checked = false;
                            }
                        }
                    }));
                deletePresetToolStripMenuItem.DropDownItems.Insert(i, new ToolStripMenuItem(preset, null,
                    delegate (object sender, EventArgs e)
                    {
                        if (MessageBox.Show(this, string.Format(Resources.DeletePreset, preset), Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                            == DialogResult.Yes)
                        {
                            ConfigIni.Instance.Presets.Remove(preset);
                            LoadPresets();
                        }
                    }));
                deletePresetToolStripMenuItem.Enabled = true;
                i++;
            }
        }

        void LoadLanguages()
        {
            var languages = new List<string>(Directory.GetDirectories(Path.Combine(Program.BaseDirectoryInternal, "languages")));
            ResourceManager rm = Resources.ResourceManager;
            languages.Add("en-US"); // default language
            var langCodes = new Dictionary<string, string>();
            foreach (var language in languages)
            {
                var code = Path.GetFileName(language);
                langCodes[new CultureInfo(code).DisplayName] = code;
            }
            ToolStripMenuItem english = null;
            bool found = false;
            foreach (var language in langCodes.Keys.OrderBy<string, string>(o => o))
            {
                var item = new ToolStripMenuItem();
                var displayName = Regex.Replace(language, @" \(.+\)", "");
                if (langCodes.Keys.Count(o => Regex.Replace(o, @" \(.+\)", "") == displayName) == 1)
                    item.Text = displayName;
                else
                    item.Text = language;
                var country = langCodes[language];
                if (langCodes[language] == "zh-CHS" || langCodes[language] == "zh-CHT") // chinese is awkward
                    country = "cn";
                else
                    if (country.Length > 2) country = country.Substring(country.Length - 2).ToLower();
                // Trying to load flag
                item.Image = (Image)rm.GetObject(country);
                if (item.Image == null)
                    Debug.WriteLine($"There is no flag for \"{country}\"");
                item.ImageScaling = ToolStripItemImageScaling.None;
                item.Click += delegate (object sender, EventArgs e)
                    {
                        ConfigIni.Instance.Language = langCodes[language];
                        SaveConfig();
                        lastConsoleType = ConsoleType.Unknown;
                        lgvGroups = null;
                        lgvAppGroups = null;
                        lgvCustomGroups = null;
                        Thread.CurrentThread.CurrentUICulture = new CultureInfo(langCodes[language]);
                        this.Controls.Clear();
                        this.InitializeComponent();
                        FormInitialize();
                        this.Invalidate(true);
                    };
                if (Thread.CurrentThread.CurrentUICulture.Name.ToUpper() == langCodes[language].ToUpper())
                {
                    item.Checked = true;
                    if (string.IsNullOrEmpty(ConfigIni.Instance.Language))
                        ConfigIni.Instance.Language = langCodes[language];
                }
                found |= item.Checked;
                if (langCodes[language] == "en-US")
                    english = item;
                languageToolStripMenuItem.DropDownItems.Add(item);
            }
            if (!found)
                english.Checked = true;
        }

        private void SaveSelectedGames(bool hideOriginalGames = false)
        {
            Debug.WriteLine("Saving selected games");
            var selected = ConfigIni.Instance.SelectedGames;
            selected.Clear();
            var hiddenSelected = ConfigIni.Instance.HiddenGames;
            if (hideOriginalGames)
            {
                Debug.WriteLine("Hiding original games");
                hiddenSelected.Clear();
            }

            foreach (ListViewItem game in listViewGames.CheckedItems)
            {
                if (game.Tag is NesApplication)
                {
                    selected.Add((game.Tag as NesApplication).Code);
                    if (hideOriginalGames && (game.Tag as NesApplication).IsOriginalGame)
                    {
                        hiddenSelected.Add((game.Tag as NesApplication).Code);
                    }
                }
            }
        }

        private void SaveConfig()
        {
            SaveSelectedGames();
            ConfigIni.Save();
            foreach (ListViewItem game in listViewGames.Items)
            {
                try
                {
                    if (game.Tag is NesApplication)
                    {
                        // Maybe type was changed? Need to reload games
                        if ((game.Tag as NesApplication).Save())
                            game.Tag = NesApplication.FromDirectory((game.Tag as NesApplication).BasePath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message + ex.StackTrace);
                    MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        void AddPreset(object sender, EventArgs e)
        {
            var form = new StringInputForm();
            form.Text = Resources.NewPreset;
            form.labelComments.Text = Resources.InputPreset;
            if (form.ShowDialog() == DialogResult.OK)
            {
                var name = form.textBox.Text.Replace("=", " ");
                if (!string.IsNullOrEmpty(name))
                {
                    SaveSelectedGames();
                    ConfigIni.Instance.Presets[name] = ConfigIni.Instance.SelectedGames.ToList();
                    LoadPresets();
                }
            }
        }

        private void listViewGames_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            int c = listViewGames.SelectedItems.Count;
            ListViewItem item = c == 1 ? listViewGames.SelectedItems[0] : null;
            if (item != null && item.Tag is NesApplication && (item.Tag as NesApplication).IsDeleting) c = 0;

            if (c == 0)
            {
                explorerToolStripMenuItem.Enabled = 
                    downloadBoxArtForSelectedGamesToolStripMenuItem.Enabled =
                    scanForNewBoxArtForSelectedGamesToolStripMenuItem.Enabled =
                    deleteSelectedGamesBoxArtToolStripMenuItem.Enabled =
                    compressSelectedGamesToolStripMenuItem.Enabled =
                    decompressSelectedGamesToolStripMenuItem.Enabled =
                    deleteSelectedGamesToolStripMenuItem.Enabled =
                    sFROMToolToolStripMenuItem1.Enabled =
                    selectEmulationCoreToolStripMenuItem.Enabled = false;
            }
            else if (c == 1)
            {
                explorerToolStripMenuItem.Enabled =
                    downloadBoxArtForSelectedGamesToolStripMenuItem.Enabled =
                    scanForNewBoxArtForSelectedGamesToolStripMenuItem.Enabled =
                    deleteSelectedGamesBoxArtToolStripMenuItem.Enabled = true;

                deleteSelectedGamesToolStripMenuItem.Enabled = 
                    selectEmulationCoreToolStripMenuItem.Enabled = !(item.Tag as NesApplication).IsOriginalGame;
                compressSelectedGamesToolStripMenuItem.Enabled = (item.Tag as NesApplication).CompressPossible().Count() > 0;
                decompressSelectedGamesToolStripMenuItem.Enabled = (item.Tag as NesApplication).DecompressPossible().Count() > 0;

                sFROMToolToolStripMenuItem1.Enabled =
                    editROMHeaderToolStripMenuItem.Enabled =
                    resetROMHeaderToolStripMenuItem.Enabled =
                        (item.Tag is SnesGame &&
                        !(item.Tag as SnesGame).IsOriginalGame &&
                        (item.Tag as SnesGame).GameFilePath.ToLower().Contains(".sfrom"));
            }
            else
            {
                explorerToolStripMenuItem.Enabled =
                    editROMHeaderToolStripMenuItem.Enabled = false;

                downloadBoxArtForSelectedGamesToolStripMenuItem.Enabled =
                    scanForNewBoxArtForSelectedGamesToolStripMenuItem.Enabled =
                    deleteSelectedGamesBoxArtToolStripMenuItem.Enabled =
                    compressSelectedGamesToolStripMenuItem.Enabled =
                    decompressSelectedGamesToolStripMenuItem.Enabled =
                    deleteSelectedGamesToolStripMenuItem.Enabled =
                    sFROMToolToolStripMenuItem1.Enabled =
                    resetROMHeaderToolStripMenuItem.Enabled = 
                    selectEmulationCoreToolStripMenuItem.Enabled = true;
            }

            if (!e.IsSelected)
                (e.Item.Tag as NesApplication).Save();

            timerShowSelected.Enabled = true;
        }

        private void timerShowSelected_Tick(object sender, EventArgs e)
        {
            timerShowSelected.Enabled = false;
            ShowSelected();
        }

        private void listViewGames_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            // Schedule recalculation
            timerCalculateGames.Enabled = false;
            timerCalculateGames.Enabled = true;
        }

        private void timerCalculateGames_Tick(object sender, EventArgs e)
        {
            new Thread(RecalculateSelectedGamesThread).Start(); // Calculate it in background
            timerCalculateGames.Enabled = false; // We don't need to count games repetedly
        }

        private void listViewGames_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.A:
                    if (e.Modifiers == Keys.Control)
                    {
                        listViewGames.BeginUpdate();
                        foreach (ListViewItem item in listViewGames.Items)
                            item.Selected = true;
                        listViewGames.EndUpdate();
                    }
                    break;
                case Keys.Delete:
                    if ((listViewGames.SelectedItems.Count > 1) || (listViewGames.SelectedItems.Count == 1 && listViewGames.SelectedItems[0].Tag is NesApplication))
                        DeleteSelectedGames();
                    break;
                case Keys.Space:
                    if (listViewGames.FocusedItem == null)
                    {
                        bool all = true;
                        foreach (ListViewItem item in listViewGames.SelectedItems)
                            if (!item.Checked)
                            {
                                all = false;
                                break;
                            }
                        foreach (ListViewItem item in listViewGames.SelectedItems)
                        {
                            item.Checked = !all;
                        }

                    }
                    break;
            }
        }

        private void listViewGames_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
                contextMenuStrip.Show(sender as Control, e.X + 5, e.Y);
        }

        private NesApplication GetSelectedGame()
        {
            if (listViewGames.SelectedItems.Count != 1) return null;
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected == null || !(selected is NesApplication)) return null;
            return selected as NesApplication;
        }

        private void SetImageForSelectedGame(string imagePath)
        {
            var app = GetSelectedGame();
            if (app != null)
            {
                app.SetImageFile(imagePath, ConfigIni.Instance.CompressCover);
                ShowSelected();
                timerCalculateGames.Enabled = true;
            }
        }

        private void buttonDefaultCover_Click(object sender, EventArgs e)
        {
            var app = GetSelectedGame();
            if (app != null) { 
                app.Image = null;
                ShowSelected();
                timerCalculateGames.Enabled = true;
            }
        }

        private void buttonBrowseImage_Click(object sender, EventArgs e)
        {
            var app = GetSelectedGame();
            if (app == null) return;

            openFileDialogImage.Filter = Resources.Images + "|*.bmp;*.png;*.jpg;*.jpeg;*.gif;*.tif;*.tiff|" + Resources.AllFiles + "|*.*";
            if (openFileDialogImage.ShowDialog() == DialogResult.OK)
            {
                app.SetImageFile(openFileDialogImage.FileName, ConfigIni.Instance.CompressCover);
                ShowSelected();
                timerCalculateGames.Enabled = true;
            }
        }

        private void pictureBoxThumbnail_Click(object sender, EventArgs e)
        {
            var app = GetSelectedGame();
            if (app == null) return;

            openFileDialogImage.Filter = Resources.Images + "|*.bmp;*.png;*.jpg;*.jpeg;*.gif;*.tif;*.tiff|" + Resources.AllFiles + "|*.*";
            if (openFileDialogImage.ShowDialog() == DialogResult.OK)
            {
                app.SetThumbnailFile(openFileDialogImage.FileName, ConfigIni.Instance.CompressCover);
                ShowSelected();
                timerCalculateGames.Enabled = true;
            }
        }

        private void buttonGoogle_Click(object sender, EventArgs e)
        {
            var app = GetSelectedGame();
            if (app == null) return;

            var googler = new ImageGooglerForm(app);
            if (googler.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                app.Image = googler.Result;
                ShowSelected();
                timerCalculateGames.Enabled = true;
            }
        }

        private void textBoxName_TextChanged(object sender, EventArgs e)
        {
            if (showingSelected) return;
            if (listViewGames.SelectedItems.Count != 1) return;
            var selectedItem = listViewGames.SelectedItems[0];
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected == null || !(selected is NesApplication)) return;
            var game = (selected as NesApplication);
            if (selectedItem.Text != textBoxName.Text)
            {
                var newSortName = textBoxName.Text.ToLower();
                if (newSortName.StartsWith("the "))
                    newSortName = newSortName.Substring(4); // Sorting without "THE"
                selectedItem.Text = game.Name = textBoxName.Text;
                textBoxSortName.Text = newSortName;
            }
        }

        private void textBoxName_Leave(object sender, EventArgs e)
        {
            listViewGames.Sort();
        }

        private void textBoxSortName_TextChanged(object sender, EventArgs e)
        {
            if (showingSelected) return;
            if (listViewGames.SelectedItems.Count != 1) return;
            var selectedItem = listViewGames.SelectedItems[0];
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected == null || !(selected is NesApplication)) return;
            var game = (selected as NesApplication);
            game.Desktop.SortName = textBoxSortName.Text = textBoxSortName.Text.ToLower();
        }

        private void radioButtonOne_CheckedChanged(object sender, EventArgs e)
        {
            if (showingSelected) return;
            if (listViewGames.SelectedItems.Count != 1) return;
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected == null || !(selected is NesApplication)) return;
            var game = (selected as NesApplication);
            game.Desktop.Players = (byte)(radioButtonOne.Checked ? 1 : 2);
            game.Desktop.Simultaneous = radioButtonTwoSim.Checked;
        }

        private void textBoxPublisher_TextChanged(object sender, EventArgs e)
        {
            if (showingSelected) return;
            if (listViewGames.SelectedItems.Count != 1) return;
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected == null || !(selected is NesApplication)) return;
            var game = (selected as NesApplication);
            game.Desktop.Publisher = textBoxPublisher.Text.ToUpper();
        }

        private void numericUpDownSaveCount_ValueChanged(object sender, EventArgs e)
        {
            if (showingSelected) return;
            if (listViewGames.SelectedItems.Count != 1) return;
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected == null || !(selected is NesApplication)) return;
            var game = (selected as NesApplication);
            if (numericUpDownSaveCount.Value < 0)
                numericUpDownSaveCount.Value = 0;
            if (numericUpDownSaveCount.Value > 3)
                numericUpDownSaveCount.Value = 3;
            game.Desktop.SaveCount = decimal.ToByte(numericUpDownSaveCount.Value);
        }

        private void textBoxArguments_TextChanged(object sender, EventArgs e)
        {
            if (showingSelected) return;
            if (listViewGames.SelectedItems.Count != 1) return;
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected == null || !(selected is NesApplication)) return;
            var game = (selected as NesApplication);
            game.Desktop.Exec = textBoxArguments.Text;
        }

        private void maskedTextBoxReleaseDate_TextChanged(object sender, EventArgs e)
        {
            if (showingSelected) return;
            if (listViewGames.SelectedItems.Count != 1) return;
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected == null || !(selected is NesApplication)) return;
            var game = (selected as NesApplication);
            game.Desktop.ReleaseDate = maskedTextBoxReleaseDate.Text;
        }

        private void textBoxGameGenie_TextChanged(object sender, EventArgs e)
        {
            if (showingSelected) return;
            if (listViewGames.SelectedItems.Count != 1) return;
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected == null || !(selected is NesApplication)) return;
            var game = (selected as NesApplication);
            game.GameGenie = textBoxGameGenie.Text;
        }

        private void buttonShowGameGenieDatabase_Click(object sender, EventArgs e)
        {
            if (listViewGames.SelectedItems.Count != 1) return;
            var selected = listViewGames.SelectedItems[0].Tag;
            if (!(selected is ISupportsGameGenie)) return;
            NesApplication nesGame = selected as NesApplication;
            GameGenieCodeForm lFrm = new GameGenieCodeForm(nesGame);
            if (lFrm.ShowDialog() == DialogResult.OK)
                textBoxGameGenie.Text = (nesGame as NesApplication).GameGenie;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Debug.WriteLine("Closing main form");
            SaveConfig();
            ftpServer.Stop();
            hakchi.Shutdown();
        }
        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            Process.GetCurrentProcess().Kill(); // Suicide! Just easy and dirty way to kill all threads.
        }

        struct CountResult
        {
            public int Count;
            public long Size;
        }
        void RecalculateSelectedGamesThread()
        {
            try
            {
                var stats = RecalculateSelectedGames();
                showStats(stats);
            }
            catch
            {
                timerCalculateGames.Enabled = false;
                timerCalculateGames.Enabled = true;
            }
        }
        CountResult RecalculateSelectedGames()
        {
            CountResult stats;
            stats.Count = 0;
            stats.Size = 0;
            if (!this.IsHandleCreated)
                return new CountResult(); ;
            var checkedGames = (IEnumerable<object>)Invoke(new Func<IEnumerable<object>>(delegate
            {
                var r = new List<object>();
                foreach (ListViewItem o in listViewGames.CheckedItems)
                    r.Add(o.Tag);
                return r;
            }));
            foreach (var game in checkedGames)
            {
                if (game is NesApplication)
                {
                    stats.Count++;
                    stats.Size += (game as NesApplication).Size();
                }
            }
            return stats;
        }
        void showStats(CountResult stats)
        {
            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action<CountResult>(showStats), new object[] { stats });
                    return;
                }
                var maxGamesSize = DefaultMaxGamesSize * 1024 * 1024;
                if (WorkerForm.StorageTotal > 0)
                {
                    maxGamesSize = (WorkerForm.StorageFree + WorkerForm.WrittenGamesSize) - WorkerForm.ReservedMemory * 1024 * 1024;
                    toolStripStatusLabelSize.Text = string.Format("{0} / {1}", Shared.SizeSuffix(stats.Size), Shared.SizeSuffix(maxGamesSize));
                }
                else
                {
                    toolStripStatusLabelSize.Text = string.Format("{0} / ???MB", Shared.SizeSuffix(stats.Size));
                }
                double usagePercentage = ((double)stats.Size / (double)maxGamesSize);
                if (usagePercentage > 1.0)
                {
                    usagePercentage = 1.0;
                }
                toolStripStatusLabelSelected.Text = stats.Count + " " + Resources.GamesSelected;
                toolStripProgressBar.Maximum = int.MaxValue;
                toolStripProgressBar.Value = Convert.ToInt32(usagePercentage * int.MaxValue);
                toolStripStatusLabelSize.ForeColor =
                    (toolStripProgressBar.Value < toolStripProgressBar.Maximum) ?
                    SystemColors.ControlText :
                    Color.Red;
            }
            catch { }
        }

        private void buttonAddGames_Click(object sender, EventArgs e)
        {
            if (openFileDialogNes.ShowDialog() == DialogResult.OK)
            {
                AddGames(openFileDialogNes.FileNames);
            }
        }

        private void reloadGamesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveSelectedGames();
            listViewGames.BeginUpdate();
            foreach (ListViewItem item in listViewGames.Items)
                item.Selected = false;
            listViewGames.EndUpdate();
            LoadGames();

        }

        DialogResult RequireKernelDump()
        {
            if (File.Exists(WorkerForm.KernelDumpPath)) return DialogResult.OK; // OK - already dumped
                                                                                // Asking user to dump kernel
            if (MessageBox.Show(Resources.NoKernelWarning, Resources.NoKernel, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == System.Windows.Forms.DialogResult.Yes)
            {
                if (DoKernelDump())
                    return DialogResult.Yes; // Succesfully dumped
                else
                    return DialogResult.No; // Not dumped for some other reason
            }
            else return DialogResult.No; // Kernel dump cancelled by user
        }

        DialogResult RequirePatchedKernel()
        {
            if (hakchi.Shell.IsOnline) return DialogResult.OK; // OK - Shell is online
            if (MessageBox.Show(Resources.CustomWarning, Resources.CustomKernel, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                    == System.Windows.Forms.DialogResult.Yes)
            {
                if (FlashCustomKernel())
                    return DialogResult.Yes; // Succesfully flashed
                else
                    return DialogResult.No; // Not flashed for some other reason
            }
            else return DialogResult.No;
        }

        private void buttonStart_Click(object sender, EventArgs e)
        {
            SaveConfig();

            var stats = RecalculateSelectedGames();
            if (stats.Count == 0)
            {
                MessageBox.Show(Resources.SelectAtLeast, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var kernel = RequirePatchedKernel();
            if (kernel == DialogResult.No) return;
            if (kernel == DialogResult.Yes) // Message for new user
                MessageBox.Show(Resources.DoneYouCanUpload + "\r\n" + Resources.PressOkToContinue, Resources.Congratulations, MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (UploadGames())
                if (!ConfigIni.Instance.DisablePopups)
                    MessageBox.Show(Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void buttonExport_Click(object sender, EventArgs e)
        {
            SaveConfig();
            var stats = RecalculateSelectedGames();
            if (stats.Count == 0)
            {
                MessageBox.Show(Resources.SelectAtLeast, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (UploadGames(true))
                if (!ConfigIni.Instance.DisablePopups)
                    MessageBox.Show(Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        bool DoKernelDump()
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.DumpingKernel;
            workerForm.Task = WorkerForm.Tasks.DumpKernel;
            workerForm.Start();
            return workerForm.DialogResult == DialogResult.OK;
        }

        

        bool DoNandFlash()
        {
            openDumpFileDialog.FileName = "nand.bin";
            openDumpFileDialog.DefaultExt = "bin";
            if (openDumpFileDialog.ShowDialog() != DialogResult.OK)
                return false;
            var workerForm = new WorkerForm(this);
            workerForm.Text = "Bricking your console";
            workerForm.Task = WorkerForm.Tasks.FlashNand;
            workerForm.NandDump = openDumpFileDialog.FileName;
            workerForm.Start();
            return workerForm.DialogResult == DialogResult.OK;
        }

        bool DumpDialog(FileAccess type, string FileName, string FileExt, out string DumpFileName)
        {
            DumpFileName = null;
            switch (type)
            {
                case FileAccess.Read:
                    openDumpFileDialog.FileName = FileName;
                    openDumpFileDialog.DefaultExt = FileExt;
                    if (openDumpFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        DumpFileName = openDumpFileDialog.FileName;
                        return true;
                    }
                    return false;

                case FileAccess.Write:
                    saveDumpFileDialog.FileName = FileName;
                    saveDumpFileDialog.DefaultExt = FileExt;
                    if (saveDumpFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        DumpFileName = saveDumpFileDialog.FileName;
                        return true;
                    }
                    return false;

                default:
                    throw new ArgumentOutOfRangeException("type");
            }
        }

        bool DoNand(WorkerForm.Tasks task)
        {
            
            var workerForm = new WorkerForm(this);
            string dumpFilename = null;
            switch (task)
            {
                case WorkerForm.Tasks.DumpNand:
                    workerForm.Text = Resources.DumpingNand;
                    if (!DumpDialog(FileAccess.Write, "nand.bin", "bin", out dumpFilename)) return false;
                    break;

                case WorkerForm.Tasks.DumpNandB:
                    workerForm.Text = Resources.DumpingNand;
                    if (!DumpDialog(FileAccess.Write, "nandb.hsqs", "hsqs", out dumpFilename)) return false;
                    break;

                case WorkerForm.Tasks.FlashNandB:
                    workerForm.Text = Resources.FlashingNand;
                    if (!DumpDialog(FileAccess.Read, "nandb.hsqs", "hsqs", out dumpFilename)) return false;
                    break;

                case WorkerForm.Tasks.DumpNandC:
                    workerForm.Text = Resources.DumpingNand;
                    if (!DumpDialog(FileAccess.Write, "nandc.hsqs", "hsqs", out dumpFilename)) return false;
                    break;

                case WorkerForm.Tasks.FlashNandC:
                    workerForm.Text = Resources.FlashingNand;
                    if (!DumpDialog(FileAccess.Read, "nandc.hsqs", "hsqs", out dumpFilename)) return false;
                    break;

                default:
                    throw new ArgumentOutOfRangeException("task");
            }
            workerForm.NandDump = dumpFilename;
            workerForm.Task = task;
            workerForm.zImage = Shared.PathCombine(Program.BaseDirectoryInternal, "data", "zImageMemboot");
            workerForm.Mod = "mod_general";
            workerForm.Config = null;
            workerForm.Games = null;
            workerForm.Start();
            return workerForm.DialogResult == DialogResult.OK;
        }

        bool FlashUboot(string customUboot = null)
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.FlashingUboot;
            workerForm.Task = WorkerForm.Tasks.FlashUboot;
            if (!string.IsNullOrEmpty(customUboot))
                workerForm.customUboot = customUboot;
            workerForm.Config = null;
            workerForm.Games = null;
            workerForm.Start();
            var result = workerForm.DialogResult == DialogResult.OK;
            return result;
        }

        bool FlashCustomKernel()
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.FlasingCustom;
            workerForm.Task = WorkerForm.Tasks.FlashKernel;
            workerForm.Mod = "mod_hakchi";
            workerForm.hmodsInstall = new List<string>(InternalMods);
            workerForm.Config = null;
            workerForm.Games = null;
            workerForm.Start();
            var result = workerForm.DialogResult == DialogResult.OK;
            return result;
        }

        bool MembootOriginalKernel()
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.Membooting;
            workerForm.Task = WorkerForm.Tasks.Memboot;
            workerForm.Mod = null;
            workerForm.Config = null;
            workerForm.Games = null;
            workerForm.Start();
            return workerForm.DialogResult == DialogResult.OK;
        }

        bool MembootCustomKernel(string mod = "mod_hakchi", string extraFiles = null, bool copyBaseMods = false)
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.Membooting;
            workerForm.Task = WorkerForm.Tasks.Memboot;
            workerForm.zImage = Shared.PathCombine(Program.BaseDirectoryInternal, "data", "zImageMemboot");
            workerForm.Mod = mod;

            if (copyBaseMods)
                workerForm.hmodsInstall = new List<string>(InternalMods);

            if (!String.IsNullOrEmpty(extraFiles))
                workerForm.ModExtraFilesPaths = new string[] { Shared.PathCombine(Program.BaseDirectoryInternal, "mods", extraFiles) };

            workerForm.Config = null;
            workerForm.Games = null;
            workerForm.Start();
            return workerForm.DialogResult == DialogResult.OK;
        }

        bool UploadGames(bool exportGames = false)
        {
            var workerForm = new WorkerForm(this);
            if (exportGames)
            {
                using (ExportGamesDialog driveSelectDialog = new ExportGamesDialog())
                {
                    if (driveSelectDialog.ShowDialog(this) != DialogResult.OK)
                        return false;

                    workerForm.linkRelativeGames = driveSelectDialog.LinkedExport;
                    workerForm.exportDirectory = driveSelectDialog.ExportPath;

                    if (!Directory.Exists(driveSelectDialog.ExportPath))
                        Directory.CreateDirectory(driveSelectDialog.ExportPath);
                }
            }
            workerForm.Text = Resources.UploadingGames;
            workerForm.Task = WorkerForm.Tasks.UploadGames;
            workerForm.Mod = "mod_hakchi";
            workerForm.Config = ConfigIni.GetConfigDictionary();
            workerForm.Games = new NesMenuCollection();
            workerForm.exportGames = exportGames;
            if (!exportGames)
                workerForm.linkRelativeGames = false;

            foreach (ListViewItem game in listViewGames.CheckedItems)
            {
                if (game.Tag is NesApplication)
                    workerForm.Games.Add(game.Tag as NesApplication);
            }

            workerForm.FoldersMode = ConfigIni.Instance.FoldersMode;
            workerForm.MaxGamesPerFolder = ConfigIni.Instance.MaxGamesPerFolder;
            workerForm.Start();
            return workerForm.DialogResult == DialogResult.OK;
        }

        void AddGames(IEnumerable<string> files)
        {
            SaveConfig();
            ICollection<NesApplication> addedApps;
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.LoadingGames;
            workerForm.Task = WorkerForm.Tasks.AddGames;
            workerForm.GamesToAdd = files;
            workerForm.Start();
            addedApps = workerForm.addedApplications;

            if (addedApps != null)
            {
                // show select core dialog if applicable
                var unknownApps = new List<NesApplication>();
                foreach(var app in addedApps)
                {
                    if (app.Metadata.AppInfo.Unknown)
                        unknownApps.Add(app);
                }
                if (unknownApps.Count > 0)
                {
                    using (SelectCoreDialog selectCoreDialog = new SelectCoreDialog())
                    {
                        selectCoreDialog.Games.AddRange(unknownApps);
                        selectCoreDialog.ShowDialog(this);
                    }
                }

                // show select cover dialog if applicable
                unknownApps.Clear();
                foreach(var app in addedApps)
                {
                    if (!app.CoverArtMatchSuccess && app.CoverArtMatches.Any())
                        unknownApps.Add(app);
                }
                if(unknownApps.Count > 0)
                {
                    using (SelectCoverDialog selectCoverDialog = new SelectCoverDialog())
                    {
                        selectCoverDialog.Games.AddRange(unknownApps);
                        selectCoverDialog.ShowDialog(this);
                    }
                }

                // update list view
                listViewGames.BeginUpdate();
                foreach (ListViewItem item in listViewGames.Items)
                    item.Selected = false;
                // add games, only new ones
                var newApps = addedApps.Distinct(new NesApplication.NesAppEqualityComparer());
                var newCodes = from app in newApps select app.Code;
                var oldAppsReplaced = from app in listViewGames.Items.Cast<ListViewItem>().ToArray()
                                      where (app.Tag is NesApplication) && newCodes.Contains((app.Tag as NesApplication).Code)
                                      select app;
                foreach (var replaced in oldAppsReplaced)
                    listViewGames.Items.Remove(replaced);
                foreach (var newApp in newApps)
                {
                    var item = new ListViewItem(newApp.Name);
                    item.Group = lgvGroups[0];
                    item.Tag = newApp;
                    item.Selected = true;
                    item.Checked = true;
                    listViewGames.Items.Add(item);
                }
                listViewGames.EndUpdate();
            }
            else
            {
                // Reload all games (maybe process was terminated?)
                LoadGames();
            }
            // Schedule recalculation
            timerCalculateGames.Enabled = false;
            timerCalculateGames.Enabled = true;
        }

        bool FlashOriginalKernel(bool boot = true)
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.FlasingOriginal;
            workerForm.Task = WorkerForm.Tasks.FlashKernel;
            workerForm.Mod = null;
            workerForm.Start();
            var result = workerForm.DialogResult == DialogResult.OK;
            return result;
        }

        bool Uninstall()
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.Uninstalling;
            workerForm.Task = WorkerForm.Tasks.Memboot;
            workerForm.zImage = Shared.PathCombine(Program.BaseDirectoryInternal, "data", "zImageMemboot");
            workerForm.Mod = "mod_uninstall";
            workerForm.Start();
            return workerForm.DialogResult == DialogResult.OK;
        }

        bool InstallMods(string[] mods)
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.InstallingMods;
            workerForm.zImage = Shared.PathCombine(Program.BaseDirectoryInternal, "data", "zImageMemboot");
            workerForm.Task = WorkerForm.Tasks.ProcessMods;
            workerForm.Mod = "mod_hakchi";
            workerForm.hmodsInstall = new List<string>(mods);
            workerForm.hmodsUninstall = new List<string>();
            workerForm.Start();
            return workerForm.DialogResult == DialogResult.OK;
        }

        bool UninstallMods(string[] mods)
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.UninstallingMods;
            workerForm.zImage = Shared.PathCombine(Program.BaseDirectoryInternal, "data", "zImageMemboot");
            workerForm.Task = WorkerForm.Tasks.ProcessMods;
            workerForm.Mod = "mod_hakchi";
            workerForm.hmodsInstall = new List<string>();
            workerForm.hmodsUninstall = new List<string>(mods);
            workerForm.Start();
            return workerForm.DialogResult == DialogResult.OK;
        }

        private void dumpKernelToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (File.Exists(WorkerForm.KernelDumpPath))
            {
                MessageBox.Show(Resources.ReplaceKernelQ, Resources.Warning, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(Resources.DumpKernelQ, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == System.Windows.Forms.DialogResult.Yes)
            {
                if (DoKernelDump()) MessageBox.Show(Resources.KernelDumped, Resources.Done, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void normalModeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(Resources.FlashUbootNormalQ, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == DialogResult.Yes)
            {
                if (FlashUboot("uboot.bin"))
                {
                    MessageBox.Show(Resources.UbootFlashed, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void sDModeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(Resources.FlashUbootSDQ, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == DialogResult.Yes)
            {
                if (FlashUboot("ubootSD.bin"))
                {
                    MessageBox.Show(Resources.UbootFlashed, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void dumpTheWholeNANDToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (DoNand(WorkerForm.Tasks.DumpNand))
                MessageBox.Show(Resources.NandDumped, Resources.Done, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void toolFlashTheWholeNANDStripMenuItem_Click(object sender, EventArgs e)
        {
            // Maybe I'll fix it one day...
            if (MessageBox.Show("It will brick your console. Do you want to continue?", Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == DialogResult.Yes)
            {
                DoNandFlash();
            }
        }

        private void dumpNANDBToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequirePatchedKernel() == DialogResult.No) return;
            if (DoNand(WorkerForm.Tasks.DumpNandB))
                MessageBox.Show(Resources.NandDumped, Resources.Done, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }


        private void flashNANDBPartitionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequirePatchedKernel() == DialogResult.No) return;
            if (DoNand(WorkerForm.Tasks.FlashNandB))
                MessageBox.Show(Resources.NandFlashed, Resources.Done, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void dumpNANDCPartitionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequirePatchedKernel() == DialogResult.No) return;
            if (DoNand(WorkerForm.Tasks.DumpNandC))
                MessageBox.Show(Resources.NandDumped, Resources.Done, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void flashNANDCPartitionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(Resources.FlashNandCQ, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == DialogResult.Yes)
            {
                if (RequirePatchedKernel() == DialogResult.No) return;
                if (DoNand(WorkerForm.Tasks.FlashNandC))
                    MessageBox.Show(Resources.NandFlashed, Resources.Done, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void formatNANDCToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(Resources.FormatNandCQ, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == DialogResult.Yes)
            {
                if (RequireKernelDump() == DialogResult.No) return;
                MembootCustomKernel(extraFiles: "mod_format", copyBaseMods: true);
            }
        }

        private void flashCustomKernelToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(Resources.CustomKernelQ, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == DialogResult.Yes)
            {
                if (FlashCustomKernel())
                {
                    MessageBox.Show(Resources.DoneYouCanUpload, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void membootOriginalKernelToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!File.Exists(WorkerForm.KernelDumpPath))
            {
                MessageBox.Show(Resources.NoKernelYouNeed, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MembootOriginalKernel();
        }


        private void membootPatchedKernelToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequireKernelDump() == DialogResult.No) return;
            MembootCustomKernel();
        }

        private void flashOriginalKernelToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!File.Exists(WorkerForm.KernelDumpPath))
            {
                MessageBox.Show(Resources.NoKernelYouNeed, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (MessageBox.Show(Resources.OriginalKernelQ, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == System.Windows.Forms.DialogResult.Yes)
            {
                if (FlashOriginalKernel())
                    MessageBox.Show(Resources.UninstallFactoryNote, Resources.Done, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void uninstallToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!File.Exists(WorkerForm.KernelDumpPath))
            {
                MessageBox.Show(Resources.NoKernelYouNeed, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (MessageBox.Show(Resources.UninstallQ1, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                == System.Windows.Forms.DialogResult.Yes)
            {
                if (Uninstall())
                    MessageBox.Show(Resources.UninstallNote, Resources.Done, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var about = new AboutBox();
            about.Text = aboutToolStripMenuItem.Text.Replace("&", "");
            about.ShowDialog();
        }

        private void gitHubPageWithActualReleasesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start("https://github.com/TeamShinkansen/hakchi2/releases");
        }

        private void fAQToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start("https://github.com/TeamShinkansen/hakchi2/wiki/FAQ");
        }

        private void donateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start("https://www.paypal.me/clusterm");
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void useExtendedFontToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.UseFont = useExtendedFontToolStripMenuItem.Checked;
        }

        private void ToolStripMenuItemArmet_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.AntiArmetLevel = epilepsyProtectionToolStripMenuItem.Checked ? (byte)2 : (byte)0;
        }

        private void cloverconHackToolStripMenuItem_Click(object sender, EventArgs e)
        {
            selectButtonCombinationToolStripMenuItem.Enabled =
                ConfigIni.Instance.ResetHack = resetUsingCombinationOfButtonsToolStripMenuItem.Checked;
        }

        private void upABStartOnSecondControllerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.FcStart = upABStartOnSecondControllerToolStripMenuItem.Checked;
        }

        private void selectButtonCombinationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            switch (ConfigIni.Instance.ConsoleType)
            {
                default:
                case ConsoleType.NES:
                case ConsoleType.Famicom:
                    {
                        var form = new SelectNesButtonsForm((SelectNesButtonsForm.NesButtons)ConfigIni.Instance.ResetCombination);
                        if (form.ShowDialog() == DialogResult.OK)
                            ConfigIni.Instance.ResetCombination = (uint)form.SelectedButtons;
                    }
                    break;
                case ConsoleType.SNES:
                case ConsoleType.SuperFamicom:
                    {
                        var form = new SelectSnesButtonsForm((SelectSnesButtonsForm.SnesButtons)ConfigIni.Instance.ResetCombination);
                        if (form.ShowDialog() == DialogResult.OK)
                            ConfigIni.Instance.ResetCombination = (uint)form.SelectedButtons;
                    }
                    break;
            }
        }

        public void ResetOriginalGamesForCurrentSystem(bool nonDestructiveSync = false)
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.ResettingOriginalGames;
            workerForm.Task = WorkerForm.Tasks.SyncOriginalGames;
            workerForm.nonDestructiveSync = nonDestructiveSync;

            SaveSelectedGames();
            if (workerForm.Start() == DialogResult.OK)
                if (!ConfigIni.Instance.DisablePopups)
                    MessageBox.Show(Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);

            LoadGames();
        }

        public void ResetOriginalGamesForAllSystems()
        {
            var workerForm = new WorkerForm(this);
            workerForm.Text = Resources.ResettingOriginalGames;
            workerForm.Task = WorkerForm.Tasks.SyncOriginalGames;
            workerForm.nonDestructiveSync = true;
            workerForm.restoreAllOriginalGames = true;

            SaveSelectedGames();
            workerForm.Start();
            AddDefaultsToSelectedGames(NesApplication.defaultNesGames, ConfigIni.Instance.SelectedGamesForConsole(ConsoleType.NES));
            AddDefaultsToSelectedGames(NesApplication.defaultFamicomGames, ConfigIni.Instance.SelectedGamesForConsole(ConsoleType.Famicom));
            AddDefaultsToSelectedGames(NesApplication.defaultSnesGames, ConfigIni.Instance.SelectedGamesForConsole(ConsoleType.SNES));
            AddDefaultsToSelectedGames(NesApplication.defaultSuperFamicomGames, ConfigIni.Instance.SelectedGamesForConsole(ConsoleType.SuperFamicom));
            LoadGames();
        }

        private void AddDefaultsToSelectedGames(NesDefaultGame[] games, ICollection<string> selectedGames)
        {
            foreach (NesDefaultGame game in games)
                if (!selectedGames.Contains(game.Code))
                    selectedGames.Add(game.Code);
        }

        private void resetOriginalGamesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(string.Format(Resources.ResetOriginalGamesQ, GetConsoleTypeName(ConfigIni.Instance.ConsoleType)), Resources.Default30games, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
                == DialogResult.Yes)
            {
                ResetOriginalGamesForCurrentSystem();
            }
        }

        private void enableAutofireToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.AutofireHack = enableAutofireToolStripMenuItem.Checked;
            if (ConfigIni.Instance.AutofireHack)
                MessageBox.Show(this, Resources.AutofireHelp1, enableAutofireToolStripMenuItem.Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void useXYOnClassicControllerAsAutofireABToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.AutofireXYHack = useXYOnClassicControllerAsAutofireABToolStripMenuItem.Checked;
        }

        private void globalCommandLineArgumentsexpertsOnluToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var menuItem = sender as ToolStripMenuItem;
            var cmdLineType = (ConfigIni.ExtraCmdLineTypes)byte.Parse(menuItem.Tag.ToString());

            var form = new StringInputForm();
            form.Text = Resources.ExtraArgsTitle + " (" + menuItem.Text + ")";
            form.labelComments.Text = Resources.ExtraArgsInfo;
            form.textBox.Text = ConfigIni.Instance.ExtraCommandLineArguments[cmdLineType];
            if (form.ShowDialog() == DialogResult.OK)
                ConfigIni.Instance.ExtraCommandLineArguments[cmdLineType] = form.textBox.Text;
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            ConfigIni.Instance.RunCount++;
            if (ConfigIni.Instance.RunCount == 1 || ConfigIni.Instance.LastVersion == "0.0.0.0")
            {
                ResetOriginalGamesForAllSystems();
                MessageBox.Show(this, Resources.FirstRun, Resources.Hello, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            // check for an update after the initial console selection / on each app start
            AutoUpdater.Start(UPDATE_XML_URL);

            // enable timers
            timerConnectionCheck.Enabled = true;
            timerCalculateGames.Enabled = true;
        }

        private void dragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private void dragDrop(object sender, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null) return;

            // Need to determine type of files
            // Maybe it's cover art?
            if (files.Length == 1)
            {
                var ext = Path.GetExtension(files[0]).ToLower();
                if (ext == ".jpg" || ext == ".png")
                {
                    SetImageForSelectedGame(files[0]);
                    return;
                }
            }

            // Maybe it's some mods?
            bool mods = false;
            foreach (var file in files)
                if (Path.GetExtension(file).ToLower() == ".hmod")
                    mods = true;
            // Maybe it's some mods in single archive?
            if (files.Length == 1)
            {
                var ext = Path.GetExtension(files[0]).ToLower();
                if (ext == ".7z" || ext == ".zip" || ext == ".rar")
                {
                    SevenZipExtractor.SetLibraryPath(Path.Combine(Program.BaseDirectoryInternal, IntPtr.Size == 8 ? @"tools\7z64.dll" : @"tools\7z.dll"));
                    using (var szExtractor = new SevenZipExtractor(files[0]))
                    {
                        foreach (var f in szExtractor.ArchiveFileNames)
                            if (Path.GetExtension(f).ToLower() == ".hmod")
                                mods = true;
                    }
                }
            }
            if (mods)
            {
                installModules(files);
                return;
            }

            // All other cases - games or apps
            var allFilesToAdd = new List<string>();
            foreach (var file in files)
                if (Directory.Exists(file))
                    allFilesToAdd.AddRange(Directory.GetFiles(file, "*.*", SearchOption.AllDirectories));
                else if (File.Exists(file))
                    allFilesToAdd.Add(file);
            if (allFilesToAdd.Count > 0)
                AddGames(allFilesToAdd);
        }

        private void searchToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var searchForm = new SearchForm(this);
            searchForm.Left = this.Left + 200;
            searchForm.Top = this.Top + 300;
            searchForm.Show();
        }

        private void enableSFROMToolToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (SfromToolWrapper.IsInstalled)
            {
                ConfigIni.Instance.UseSFROMTool = enableSFROMToolToolStripMenuItem.Checked;
                usePCMPatchWhenAvailableToolStripMenuItem.Enabled = enableSFROMToolToolStripMenuItem.Checked;
            }
            else
            {
                ConfigIni.Instance.UseSFROMTool = enableSFROMToolToolStripMenuItem.Checked = false;
                usePCMPatchWhenAvailableToolStripMenuItem.Enabled = false;

                if (MessageBox.Show(string.Format(Resources.DownloadSfromTool, Program.BaseDirectoryExternal), Resources.SfromTool, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Process.Start("http://darkakuma.z-net.us/p/sfromtool.html");
            }

            sFROMToolToolStripMenuItem1.Enabled = ConfigIni.Instance.UseSFROMTool && SfromToolWrapper.IsInstalled;
        }

        private void usePCMPatchWhenAvailableToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.UsePCMPatch = usePCMPatchWhenAvailableToolStripMenuItem.Checked;
        }

        private void compressGamesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.Compress = compressGamesToolStripMenuItem.Checked;
        }

        private void compressBoxArtToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.CompressCover = compressBoxArtToolStripMenuItem.Checked;
        }

        private void centerBoxArtThumbnailToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.CenterThumbnail = centerBoxArtThumbnailToolStripMenuItem.Checked;
        }

        private void separateGamesForMultibootToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.SeparateGameStorage = separateGamesForMultibootToolStripMenuItem.Checked;
        }

        private void disableHakchi2PopupsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.DisablePopups = disableHakchi2PopupsToolStripMenuItem.Checked;
        }

        private void useLinkedSyncToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.SyncLinked = useLinkedSyncToolStripMenuItem.Checked;
        }

        private void pagesModefoldersToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.FoldersMode = (NesMenuCollection.SplitStyle)byte.Parse((sender as ToolStripMenuItem).Tag.ToString());
            disablePagefoldersToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 0;
            automaticToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 2;
            automaticOriginalToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 3;
            pagesToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 4;
            pagesOriginalToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 5;
            foldersToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 6;
            foldersOriginalToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 7;
            foldersSplitByFirstLetterToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 8;
            foldersSplitByFirstLetterOriginalToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 9;
            customToolStripMenuItem.Checked = (byte)ConfigIni.Instance.FoldersMode == 99;
        }

        private void installModulesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            installModules();
        }

        private void installModules(string[] add = null)
        {
            if (RequireKernelDump() == DialogResult.No) return;
            var form = new SelectModsForm(false, true, add);
            form.Text = Resources.SelectModsInstall;
            if (form.ShowDialog() == DialogResult.OK)
            {
                List<string> hmods = new List<string>();
                foreach (ListViewItem item in form.listViewHmods.CheckedItems)
                {
                    hmods.Add(((Hmod)item.Tag).RawName);
                }
                if (InstallMods(hmods.ToArray()))
                {
                    if (!ConfigIni.Instance.DisablePopups)
                        MessageBox.Show(Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void uninstallModulesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequireKernelDump() == DialogResult.No) return;
            var form = new SelectModsForm(true, false);
            form.Text = Resources.SelectModsUninstall;
            if (form.ShowDialog() == DialogResult.OK)
            {
                List<string> hmods = new List<string>();
                foreach (ListViewItem item in form.listViewHmods.CheckedItems)
                {
                    hmods.Add(((Hmod)item.Tag).RawName);
                }
                if (UninstallMods(hmods.ToArray()))
                {
                    if (!ConfigIni.Instance.DisablePopups)
                        MessageBox.Show(Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void timerConnectionCheck_Tick(object sender, EventArgs e)
        {
            toolStripStatusConnectionIcon.Image = hakchi.Shell.IsOnline ? Resources.green : Resources.red;
            toolStripStatusConnectionIcon.ToolTipText = hakchi.Shell.IsOnline ? "Online" : "Offline";
        }

        private void saveSettingsToNESMiniNowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequirePatchedKernel() == DialogResult.No) return;
            try
            {
                if (WaitingClovershellForm.WaitForDevice(this))
                {
                    hakchi.SyncConfig(ConfigIni.GetConfigDictionary(), true);
                    if (!ConfigIni.Instance.DisablePopups)
                        MessageBox.Show(Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void saveStateManagerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequirePatchedKernel() == DialogResult.No) return;
            var gameNames = new Dictionary<string, string>();
            foreach (var game in NesApplication.AllDefaultGames)
                gameNames[game.Code] = game.Name;
            foreach (ListViewItem item in listViewGames.Items)
            {
                if (item.Tag is NesApplication)
                    gameNames[(item.Tag as NesApplication).Code] = (item.Tag as NesApplication).Name;
            }
            var form = new SaveStateManager(gameNames);
            form.ShowDialog();
        }

        private void FTPToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (FTPToolStripMenuItem.Checked)
            {
                try
                {
                    var ftpThread = new Thread(delegate ()
                    {
                        try
                        {
                            ftpServer.Run();
                        }
                        catch (ThreadAbortException)
                        {
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                ftpServer.Stop();
                            }
                            catch { }
                            Debug.WriteLine(ex.Message + ex.StackTrace);
                            Invoke(new Action(delegate ()
                                {
                                    MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                                    FTPToolStripMenuItem.Checked = false;
                                }));
                        }
                    });
                    ftpThread.Start();
                    ConfigIni.Instance.FtpServer = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message + ex.StackTrace);
                    MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    FTPToolStripMenuItem.Checked = false;
                    ConfigIni.Instance.FtpServer = false;
                }
            }
            else
            {
                ftpServer.Stop();
                ConfigIni.Instance.FtpServer = false;
            }
            openFTPInExplorerToolStripMenuItem.Enabled = FTPToolStripMenuItem.Checked;
        }

        private void shellToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                ConfigIni.Instance.TelnetServer = openTelnetToolStripMenuItem.Enabled = hakchi.Shell.ShellEnabled = shellToolStripMenuItem.Checked;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
                ConfigIni.Instance.TelnetServer = openTelnetToolStripMenuItem.Enabled = shellToolStripMenuItem.Checked = false;
            }
        }

        private void openFTPInExplorerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                new Process()
                {
                    StartInfo = new ProcessStartInfo()
                    {
                        FileName = String.Format(ConfigIni.Instance.FtpCommand, "root", "clover", "127.0.0.1", "1021"),
                        Arguments = String.Format(ConfigIni.Instance.FtpArguments, "root", "clover", "127.0.0.1", "1021"),
                        
                    }
                }.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void openTelnetToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                new Process()
                {
                    StartInfo = new ProcessStartInfo()
                    {
                        FileName = String.Format(ConfigIni.Instance.TelnetCommand, "127.0.0.1", "1023"),
                        Arguments = String.Format(ConfigIni.Instance.TelnetArguments, "127.0.0.1", "1023"),
                    }
                }.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, Resources.NoTelnet, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void takeScreenshotToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequirePatchedKernel() == DialogResult.No) return;
            try
            {
                if (WaitingClovershellForm.WaitForDevice(this))
                {
                    var screenshot = WorkerForm.TakeScreenshot();
                    var screenshotPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".png");
                    screenshot.Save(screenshotPath, ImageFormat.Png);
                    var showProcess = new Process()
                    {
                        StartInfo = new ProcessStartInfo()
                        {
                            FileName = screenshotPath
                        }
                    };
                    showProcess.Start();
                    new Thread(delegate ()
                    {
                        try
                        {
                            showProcess.WaitForExit();
                        }
                        catch { }
                        try
                        {
                            File.Delete(screenshotPath);
                        }
                        catch { }
                    }).Start();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void checkBoxCompressed_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                if (listViewGames.SelectedItems.Count != 1) return;
                var selected = listViewGames.SelectedItems[0].Tag;
                checkBoxCompressed.Enabled = false;
                if (checkBoxCompressed.Checked)
                    (selected as NesApplication).Compress();
                else
                    (selected as NesApplication).Decompress();
                (selected as NesApplication).Save();
                timerCalculateGames.Enabled = true;
                ShowSelected();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        bool GroupTaskWithSelected(WorkerForm.Tasks task)
        {
            var workerForm = new WorkerForm(this);
            switch (task)
            {
                case WorkerForm.Tasks.ScanCovers:
                    workerForm.Text = Resources.ScanningCovers;
                    break;
                case WorkerForm.Tasks.DownloadCovers:
                    workerForm.Text = Resources.DownloadAllCoversTitle;
                    break;
                case WorkerForm.Tasks.DeleteCovers:
                    workerForm.Text = Resources.RemovingCovers;
                    break;
                case WorkerForm.Tasks.CompressGames:
                    workerForm.Text = Resources.CompressingGames;
                    break;
                case WorkerForm.Tasks.DecompressGames:
                    workerForm.Text = Resources.DecompressingGames;
                    break;
                case WorkerForm.Tasks.DeleteGames:
                    workerForm.Text = Resources.RemovingGames;
                    break;
                case WorkerForm.Tasks.ResetROMHeaders:
                    workerForm.Text = Resources.ResettingHeaders;
                    break;
            }
            workerForm.Task = task;
            workerForm.Games = new NesMenuCollection();
            foreach (ListViewItem game in listViewGames.SelectedItems)
            {
                if (game.Tag is NesApplication)
                    workerForm.Games.Add(game.Tag as NesApplication);
            }
            return workerForm.Start() == DialogResult.OK;
        }


        private void explorerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var sel = listViewGames.SelectedItems;
            if (sel.Count != 1) return;

            try
            {
                string path = (sel[0].Tag as NesApplication).BasePath;
                new Process()
                {
                    StartInfo = new ProcessStartInfo()
                    {
                        FileName = path
                    }
                }.Start();
            }
            catch(Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
            }
        }

        private void scanForNewBoxArtForSelectedGamesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (GroupTaskWithSelected(WorkerForm.Tasks.ScanCovers))
                if (!ConfigIni.Instance.DisablePopups)
                    MessageBox.Show(this, Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowSelected();
            timerCalculateGames.Enabled = true;
        }

        private void downloadBoxArtForSelectedGamesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (GroupTaskWithSelected(WorkerForm.Tasks.DownloadCovers))
                if (!ConfigIni.Instance.DisablePopups)
                    MessageBox.Show(this, Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowSelected();
            timerCalculateGames.Enabled = true;
        }

        private void deleteSelectedGamesBoxArtToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (GroupTaskWithSelected(WorkerForm.Tasks.DeleteCovers))
                if (!ConfigIni.Instance.DisablePopups)
                    MessageBox.Show(this, Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowSelected();
            timerCalculateGames.Enabled = true;
        }

        private void compressSelectedGamesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (GroupTaskWithSelected(WorkerForm.Tasks.CompressGames))
                if (!ConfigIni.Instance.DisablePopups)
                    MessageBox.Show(this, Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowSelected();
            timerCalculateGames.Enabled = true;
        }

        private void decompressSelectedGamesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (GroupTaskWithSelected(WorkerForm.Tasks.DecompressGames))
                if(!ConfigIni.Instance.DisablePopups)
                    MessageBox.Show(this, Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowSelected();
            timerCalculateGames.Enabled = true;
        }

        private void DeleteSelectedGames()
        {
            if (MessageBox.Show(this, Resources.DeleteSelectedGamesQ, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
            {
                SaveSelectedGames();
                if (GroupTaskWithSelected(WorkerForm.Tasks.DeleteGames))
                {
                    listViewGames.BeginUpdate();
                    foreach (ListViewItem item in listViewGames.SelectedItems)
                        if (item.Tag is NesApplication && !(item.Tag as NesApplication).IsOriginalGame)
                            listViewGames.Items.Remove(item);
                    listViewGames.EndUpdate();
                    if (!ConfigIni.Instance.DisablePopups)
                        MessageBox.Show(this, Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                    LoadGames();

                ShowSelected();
                timerCalculateGames.Enabled = true;
            }
        }

        private void deleteSelectedGamesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DeleteSelectedGames();
        }

        private void editROMHeaderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listViewGames.SelectedItems.Count != 1) return;
            var selected = listViewGames.SelectedItems[0].Tag;
            if (selected is SnesGame && !(selected as SnesGame).IsOriginalGame)
            {
                SnesGame game = selected as SnesGame;
                if (ConfigIni.Instance.UseSFROMTool && SfromToolWrapper.IsInstalled)
                {
                    bool wasCompressed = game.DecompressPossible().Length > 0;
                    if (wasCompressed)
                        game.Decompress();
                    SfromToolWrapper.EditSFROM(game.GameFilePath);
                    if (wasCompressed)
                        game.Compress();
                }
                else
                {
                    new SnesPresetEditor(game).ShowDialog();
                }
                ShowSelected();
            }
        }

        private void resetROMHeaderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, Resources.ResetROMHeaderSelectedGamesQ, Resources.AreYouSure, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == System.Windows.Forms.DialogResult.Yes)
            {
                SaveSelectedGames();
                if (GroupTaskWithSelected(WorkerForm.Tasks.ResetROMHeaders))
                {
                    if (!ConfigIni.Instance.DisablePopups)
                        MessageBox.Show(this, Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.F4:
                    explorerToolStripMenuItem_Click(sender, e);
                    break;
                case Keys.E:
                    if (e.Modifiers == (Keys.Alt | Keys.Control))
                    {
                        editROMHeaderToolStripMenuItem_Click(sender, e);
                    }
                    break;
            }
        }

        private void originalGamesPositionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem menuItem = sender as ToolStripMenuItem;
            if (menuItem.Checked) return;

            OriginalGamesPosition newPosition = (OriginalGamesPosition)byte.Parse(menuItem.Tag.ToString());
            ConfigIni.Instance.OriginalGamesPosition = newPosition;

            positionAtTheTopToolStripMenuItem.Checked = newPosition == OriginalGamesPosition.AtTop;
            positionAtTheBottomToolStripMenuItem.Checked = newPosition == OriginalGamesPosition.AtBottom;
            positionSortedToolStripMenuItem.Checked = newPosition == OriginalGamesPosition.Sorted;
            positionHiddenToolStripMenuItem.Checked = newPosition == OriginalGamesPosition.Hidden;

            SaveSelectedGames(positionHiddenToolStripMenuItem.Checked);
            LoadGames();
        }

        private void groupByAppTypeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.GroupGamesByAppType = groupByAppTypeToolStripMenuItem.Checked;
            SaveSelectedGames();
            LoadGames();
        }

        private void foldersManagerToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveSelectedGames();
            var workerForm = new WorkerForm(this);
            workerForm.Games = new NesMenuCollection();

            foreach (ListViewItem game in listViewGames.CheckedItems)
            {
                if (game.Tag is NesApplication)
                    workerForm.Games.Add(game.Tag as NesApplication);
            }

            workerForm.FoldersMode = ConfigIni.Instance.FoldersMode;
            workerForm.MaxGamesPerFolder = ConfigIni.Instance.MaxGamesPerFolder;
            workerForm.FoldersManagerFromThread(workerForm.Games);
        }

        private void changeBootImageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (WaitingClovershellForm.WaitForDevice(this))
                {
                    using (OpenFileDialog ofdPng = new OpenFileDialog())
                    {
                        ofdPng.Filter = "Image files|*.bmp;*.gif;*.jpg;*.jpeg;*.png;*.tif;*.tiff";
                        if (ofdPng.ShowDialog(this) != DialogResult.OK) return;

                        string imageFile = ofdPng.FileName;
                        using (Image image = Image.FromFile(imageFile))
                        {
                            if (Path.GetExtension(imageFile) != ".png" || image.Height != 720 || image.Width != 1280)
                            {
                                var outImage = Shared.ResizeImage(image, PixelFormat.Format24bppRgb, null, 1280, 720, true, false, true, true);
                                imageFile = Shared.PathCombine(Path.GetTempPath(), "hakchi-temp", "tempBootImage.png");
                                try
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(imageFile));
                                    File.Delete(imageFile);
                                }
                                catch { }
                                outImage.Save(imageFile, ImageFormat.Png);
                            }
                        }

                        hakchi.Shell.Execute("hakchi unset cfg_boot_logo; cat > \"$(hakchi get rootfs)/etc/boot.png\"", File.OpenRead(imageFile));
                        bool usbHost = hakchi.Shell.ExecuteSimple("if [ -d /media/hakchi/ ]; then echo 1; else echo 0; fi;").Equals("1");
                        if (usbHost)
                        {
                            hakchi.Shell.Execute("cat > \"/media/hakchi/boot.png\"", File.OpenRead(imageFile));
                        }

                        if (!ConfigIni.Instance.DisablePopups)
                            MessageBox.Show(Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void disableBootImageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequirePatchedKernel() == DialogResult.No) return;
            try
            {
                if (WaitingClovershellForm.WaitForDevice(this))
                {
                    var assembly = GetType().Assembly;

                    hakchi.Shell.Execute("hakchi unset cfg_boot_logo; cat > \"$(hakchi get rootfs)/etc/boot.png\"", File.OpenRead(Shared.PathCombine(Program.BaseDirectoryInternal, "data", "blankBoot.png")));
                    bool usbHost = hakchi.Shell.ExecuteSimple("if [ -d /media/hakchi/ ]; then echo 1; else echo 0; fi;").Equals("1");
                    if (usbHost)
                    {
                        hakchi.Shell.Execute("cat > \"/media/hakchi/boot.png\"", File.OpenRead(Shared.PathCombine(Program.BaseDirectoryInternal, "data", "blankBoot.png")));
                    }

                    if (!ConfigIni.Instance.DisablePopups)
                        MessageBox.Show(Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void resetDefaultBootImageToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequirePatchedKernel() == DialogResult.No) return;
            try
            {
                if (WaitingClovershellForm.WaitForDevice(this))
                {
                    hakchi.Shell.ExecuteSimple("hakchi unset cfg_boot_logo; rm \"$(hakchi get rootfs)/etc/boot.png\"");
                    hakchi.Shell.ExecuteSimple("rm \"/media/hakchi/boot.png\"");

                    if (!ConfigIni.Instance.DisablePopups)
                        MessageBox.Show(Resources.Done, Resources.Wow, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message + ex.StackTrace);
                MessageBox.Show(this, ex.Message, Resources.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void resetToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (RequireKernelDump() == DialogResult.No) return;
            MembootCustomKernel(extraFiles: "mod_reset", copyBaseMods: true);
        }

        private void selectEmulationCoreToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (listViewGames.SelectedItems.Count == 0)
                return;

            SaveSelectedGames();
            using (SelectCoreDialog selectCoreDialog = new SelectCoreDialog())
            {
                foreach (ListViewItem item in listViewGames.SelectedItems)
                {
                    if (!(item.Tag as NesApplication).IsOriginalGame)
                    {
                        selectCoreDialog.Games.Add(item.Tag as NesApplication);
                        item.Selected = false;
                    }
                }
                if (selectCoreDialog.Games.Count == 0)
                    return;

                selectCoreDialog.ShowDialog(this);
            }
            LoadGames();
        }

        private void addCustomAppToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (NewCustomGameForm customGameForm = new NewCustomGameForm())
            {
                if (customGameForm.ShowDialog(this) == DialogResult.OK)
                {
                    var item = new ListViewItem(customGameForm.NewApp.Name);
                    item.Group = lgvGroups[0];
                    item.Tag = customGameForm.NewApp;
                    item.Selected = true;
                    item.Checked = true;

                    foreach(ListViewItem i in listViewGames.Items)
                        i.Selected = false;
                    listViewGames.Items.Add(item);
                }
            }
        }

        private void prepareArtDirectoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var selectSystemDialog = new SelectSystemDialog())
            {
                selectSystemDialog.ShowDialog();
            }
        }

        private void structureButton_Click(object sender, EventArgs e)
        {
            var button = sender as Button;
            Point ptLowerLeft = button.PointToScreen(new Point(1, button.Height));
            foldersContextMenuStrip.Show(ptLowerLeft);
        }

        private void enableUSBHostToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ConfigIni.Instance.UsbHost = enableUSBHostToolStripMenuItem.Checked;
        }

        private void gamesConsoleComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            string selected = gamesConsoleComboBox.SelectedItem as string;
            foreach (ConsoleType c in Enum.GetValues(typeof(ConsoleType)))
            {
                if (GetConsoleTypeName(c) == selected)
                {
                    if (ConfigIni.Instance.ConsoleType != c)
                    {
                        SaveSelectedGames();

                        ConfigIni.Instance.ConsoleType = c;
                        SyncConsoleType();
                        return;
                    }
                }
            }

        }
    }
}
