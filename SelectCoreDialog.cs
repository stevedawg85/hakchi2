﻿using com.clusterrr.hakchi_gui.Properties;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace com.clusterrr.hakchi_gui
{
    public partial class SelectCoreDialog : Form
    {
        public List<NesApplication> Games = new List<NesApplication>();

        private void fillGames()
        {
            listViewGames.Items.Clear();
            foreach(var game in Games)
            {
                var core = CoreCollection.GetCore(game.Metadata.Core);
                var filename = Path.GetFileName(game.GameFilePath);
                if (!string.IsNullOrEmpty(filename))
                {
                    if (filename.EndsWith(".7z"))
                        filename = filename.Substring(0, filename.Length - 3);
                    var item = new ListViewItem(new string[] { game.Name, Path.GetExtension(filename), game.Metadata.System, core == null ? string.Empty : core.Name });
                    item.Tag = game;
                    listViewGames.Items.Add(item);
                }
            }
        }

        private void fillSystems()
        {
            listBoxSystem.BeginUpdate();
            listBoxSystem.Items.Clear();
            listBoxSystem.Items.Add(Resources.Unassigned);
            var collection = showAllSystemsCheckBox.Checked || firstSelected == null ? CoreCollection.Systems : CoreCollection.GetSystemsFromExtension(firstSelected.SubItems[1].Text.ToLower()).ToArray();
            foreach (var system in collection)
            {
                listBoxSystem.Items.Add(system);
            }
            listBoxSystem.Enabled = true;
            listBoxSystem.EndUpdate();
        }

        private void fillCores(string system)
        {
            listBoxCore.BeginUpdate();
            listBoxCore.Items.Clear();
            var collection = string.IsNullOrEmpty(system) ? CoreCollection.Cores : CoreCollection.GetCoresFromSystem(system);
            //var collection = firstSelected == null ? CoreCollection.Cores : CoreCollection.GetCoresFromExtension(firstSelected.SubItems[1].Text.ToLower()).ToArray();
            if (collection != null)
            {
                foreach (var core in collection)
                {
                    listBoxCore.Items.Add(core);
                }
            }
            listBoxCore.Enabled = true;
            listBoxCore.EndUpdate();
        }

        public SelectCoreDialog()
        {
            InitializeComponent();
            Icon = Resources.icon;
            DialogResult = DialogResult.Abort;
        }

        private void SelectCoreDialog_Shown(object sender, EventArgs e)
        {
            fillGames();
            ShowSelected();
        }

        private void listBoxSystem_SelectedIndexChanged(object sender, EventArgs e)
        {
            listBoxCore.ClearSelected();
            listBoxCore.Items.Clear();

            var system = (string)listBoxSystem.SelectedItem;
            if (system != null)
            {
                fillCores(system == Resources.Unassigned ? string.Empty : system);

                if (firstSelected != null)
                {
                    var game = firstSelected.Tag as NesApplication;
                    var core = string.IsNullOrEmpty(game.Metadata.Core) ? AppTypeCollection.GetAppBySystem(system).DefaultCore : game.Metadata.Core;
                    for (int i = 0; i < listBoxCore.Items.Count; ++i)
                    {
                        if ((listBoxCore.Items[i] as CoreCollection.CoreInfo).Bin == core)
                        {
                            listBoxCore.SetSelected(i, true);
                            break;
                        }
                    }
                }
            }
        }

        private void listBoxCore_SelectedIndexChanged(object sender, EventArgs e)
        {
            ShowCommand();
            if (listBoxCore.SelectedItem == null)
            {
                buttonApply.Enabled = false;
                resetCheckBox.Enabled = false;
            }
            else
            {
                buttonApply.Enabled = true;
                resetCheckBox.Enabled = true;
            }
        }

        private ListViewItem firstSelected = null;
        private void ShowSelected()
        {
            var items = listViewGames.SelectedItems;
            if(items.Count == 0)
            {
                listBoxSystem.ClearSelected();
                commandTextBox.Text = string.Empty;
                listBoxSystem.Enabled = false;
                listBoxCore.Enabled = false;
                commandTextBox.Enabled = false;
                buttonApply.Enabled = false;
                resetCheckBox.Enabled = false;
                firstSelected = null;
            }
            else
            {
                if(items.Count == 1)
                {
                    var item = firstSelected = items[0];
                    var game = item.Tag as NesApplication;
                    fillSystems();
                    if (!string.IsNullOrEmpty(game.Metadata.System))
                    {
                        listBoxSystem.ClearSelected();
                        for (int i = 0; i < listBoxSystem.Items.Count; ++i)
                        {
                            if (listBoxSystem.Items[i].ToString() == game.Metadata.System)
                            {
                                listBoxSystem.SetSelected(i, true);
                                break;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(game.Metadata.Core))
                    {
                        for(int i = 0; i < listBoxCore.Items.Count; ++i)
                        {
                            if (listBoxCore.Items[i].ToString() == game.Metadata.Core)
                            {
                                listBoxCore.SetSelected(i, true);
                                break;
                            }
                        }
                    }
                    buttonApply.Enabled = false;

                }
                else
                {
                    ShowCommand();
                }
            }
        }

        private void ShowCommand()
        {
            var items = listViewGames.SelectedItems;
            if(items.Count == 0 || listBoxCore.SelectedItem == null)
            {
                commandTextBox.Text = string.Empty;
                commandTextBox.Enabled = false;
                return;
            }

            var core = listBoxCore.SelectedItem as CoreCollection.CoreInfo;
            if (items.Count > 1)
            {
                string newCommand = core.QualifiedBin + " {rom}";
                if (resetCheckBox.Checked)
                {
                    newCommand += string.IsNullOrEmpty(core.DefaultArgs) ? string.Empty : (" " + core.DefaultArgs);
                }
                else
                {
                    newCommand += " {args}";
                }
                
                commandTextBox.Text = newCommand.Trim();
                commandTextBox.Enabled = true;
            }
            else if (items.Count == 1)
            {
                var item = items[0];
                var game = item.Tag as NesApplication;
                if (resetCheckBox.Checked)
                {
                    string newCommand = core.QualifiedBin;
                    if (game.Desktop.Args.Length > 0)
                        newCommand += " " + game.Desktop.Args[0];
                    newCommand += " " + core.DefaultArgs;
                    commandTextBox.Text = newCommand.Trim();
                }
                else
                {
                    DesktopFile desktop = (DesktopFile)game.Desktop.Clone();
                    desktop.Bin = core.QualifiedBin;
                    commandTextBox.Text = desktop.Exec.Trim();
                }
                commandTextBox.Enabled = true;
            }
            buttonApply.Enabled = true;
        }


        private void listViewGames_SelectedIndexChanged(object sender, EventArgs e)
        {
            ShowSelected();
        }

        private void buttonApply_Click(object sender, EventArgs e)
        {
            var core = (CoreCollection.CoreInfo)listBoxCore.SelectedItem;
            string system = (string)listBoxSystem.SelectedItem;
            if (system == Resources.Unassigned)
                system = string.Empty;
            string newCommand = commandTextBox.Text;
            foreach(ListViewItem item in listViewGames.SelectedItems)
            {
                item.SubItems[2].Text = system;
                item.SubItems[3].Text = core.Name;

                var game = item.Tag as NesApplication;
                game.Metadata.System = system;
                game.Metadata.Core = core.Bin;
                game.Desktop.Exec = newCommand.Replace("{rom}", game.Desktop.Args[0]).Replace("{args}", string.Join(" ", game.Desktop.Args.Skip(1).ToArray())).Trim();
            }
            buttonApply.Enabled = false;
        }

        private void resetCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            ShowCommand();
        }

        private void commandTextBox_Enter(object sender, EventArgs e)
        {
            buttonApply.Enabled = true;
        }

        private void showAllSystemsCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            fillSystems();
            ShowSelected();
        }

        private void buttonAccept_Click(object sender, EventArgs e)
        {
            if (buttonApply.Enabled)
            {
                var result = MessageBox.Show(Resources.ApplyChangesQ, Resources.ApplyChanges, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
                if (result == DialogResult.Cancel)
                    return;
                if (result == DialogResult.Yes)
                    buttonApply_Click(sender, e);
            }

            // save game changes
            foreach (var game in Games)
            {
                game.Save();
                game.SaveMetadata();
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private void buttonDiscard_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void SelectCoreDialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (buttonApply.Enabled && DialogResult != DialogResult.OK && MessageBox.Show(Resources.DiscardChangesQ, Resources.DiscardChanges, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.No)
            {
                e.Cancel = true;
            }
        }

        private ColumnHeader SortingColumn = null;
        private void listViewGames_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            // Get the new sorting column.
            ColumnHeader new_sorting_column = listViewGames.Columns[e.Column];
            SortOrder sort_order;
            if (SortingColumn == null)
            {
                // New column. Sort ascending.
                sort_order = SortOrder.Ascending;
            }
            else
            {
                // See if this is the same column.
                if (new_sorting_column == SortingColumn)
                {
                    // Same column. Switch the sort order.
                    if (SortingColumn.Text.EndsWith(" \u25BC"))
                    {
                        sort_order = SortOrder.Descending;
                    }
                    else
                    {
                        sort_order = SortOrder.Ascending;
                    }
                }
                else
                {
                    // New column. Sort ascending.
                    sort_order = SortOrder.Ascending;
                }

                // Remove the old sort indicator.
                SortingColumn.Text = SortingColumn.Text.Substring(0, SortingColumn.Text.Length - 2);
            }

            // Display the new sort order.
            SortingColumn = new_sorting_column;
            if (sort_order == SortOrder.Ascending)
            {
                SortingColumn.Text = SortingColumn.Text + " \u25BC";
            }
            else
            {
                SortingColumn.Text = SortingColumn.Text + " \u25B2";
            }

            // Create a comparer.
            listViewGames.ListViewItemSorter =
                new ListViewItemComparer(e.Column, sort_order);

            // Sort.
            listViewGames.Sort();
        }

        // Compares two ListView items based on a selected column.
        public class ListViewItemComparer : IComparer
        {
            private int ColumnNumber;
            private SortOrder SortOrder;

            public ListViewItemComparer(int column_number, SortOrder sort_order)
            {
                ColumnNumber = column_number;
                SortOrder = sort_order;
            }

            // Compare two ListViewItems.
            public int Compare(object object_x, object object_y)
            {
                // Get the objects as ListViewItems.
                ListViewItem item_x = object_x as ListViewItem;
                ListViewItem item_y = object_y as ListViewItem;

                // Get the corresponding sub-item values.
                string string_x;
                if (item_x.SubItems.Count <= ColumnNumber)
                {
                    string_x = "";
                }
                else
                {
                    string_x = item_x.SubItems[ColumnNumber].Text;
                }

                string string_y;
                if (item_y.SubItems.Count <= ColumnNumber)
                {
                    string_y = "";
                }
                else
                {
                    string_y = item_y.SubItems[ColumnNumber].Text;
                }

                // Compare them.
                int result;
                result = string_x.CompareTo(string_y);

                // Return the correct result depending on whether
                // we're sorting ascending or descending.
                if (SortOrder == SortOrder.Ascending)
                {
                    return result;
                }
                else
                {
                    return -result;
                }
            }
        }
    }
}
