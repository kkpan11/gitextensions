﻿using GitCommands;
using GitCommands.Config;
using GitCommands.Remotes;
using GitCommands.UserRepositoryHistory;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;
using GitExtUtils.GitUI;
using GitExtUtils.GitUI.Theming;
using GitUI.Infrastructure;
using GitUI.Properties;
using GitUI.Theming;
using GitUI.UserControls;
using Microsoft;
using ResourceManager;

namespace GitUI.CommandsDialogs
{
    public partial class FormRemotes : GitModuleForm
    {
        private sealed class SortableGitRefList : SortableBindingList<IGitRef>
        {
            static SortableGitRefList()
            {
                AddSortableProperty(gitRef => gitRef.LocalName, (x, y) => string.Compare(x.LocalName, y.LocalName, StringComparison.Ordinal));
                AddSortableProperty(gitRef => gitRef.TrackingRemote, (x, y) => string.Compare(x.TrackingRemote, y.TrackingRemote, StringComparison.Ordinal));
                AddSortableProperty(gitRef => gitRef.MergeWith, (x, y) => string.Compare(x.MergeWith, y.MergeWith, StringComparison.Ordinal));
            }
        }

        private readonly FormRemotesController _formRemotesController = new();
        private IConfigFileRemoteSettingsManager? _remotesManager;
        private ConfigFileRemote? _selectedRemote;
        private readonly ListViewGroup _lvgEnabled;
        private readonly ListViewGroup _lvgDisabled;

        private string[] _genericRemotesNames = ["origin", "upstream", "fork", "remote", "internal", .. AppSettings.CustomGenericRemoteNames];

        #region Translation
        private readonly TranslationString _remoteBranchDataError =
            new("Invalid ´{1}´ found for branch ´{0}´." + Environment.NewLine +
                                  "Value has been reset to empty value.");

        private readonly TranslationString _questionAutoPullBehaviour =
            new("You have added a new remote repository." + Environment.NewLine +
                                  "Do you want to automatically configure the default push and pull behavior for this remote?");

        private readonly TranslationString _questionAutoPullBehaviourCaption =
            new("New remote");

        private readonly TranslationString _gitMessage =
          new("Message");

        private readonly TranslationString _questionDeleteRemote =
            new("Are you sure you want to delete this remote?");

        private readonly TranslationString _questionDeleteRemoteCaption =
            new("Delete");

        private readonly TranslationString _sshKeyOpenFilter =
            new("Private key (*.ppk)");

        private readonly TranslationString _sshKeyOpenCaption =
            new("Select ssh key file");

        private readonly TranslationString _labelUrlAsFetch =
            new("Fetch &Url");

        private readonly TranslationString _labelUrlAsFetchPush =
            new("&Url");

        private readonly TranslationString _gbMgtPanelHeaderNew =
            new("Create New Remote");

        private readonly TranslationString _gbMgtPanelHeaderEdit =
            new("Edit Remote Details");

        private readonly TranslationString _btnDeleteTooltip =
            new("Delete the selected remote");

        private readonly TranslationString _btnNewTooltip =
            new("Add a new remote");

        private readonly TranslationString _btnToggleStateTooltip_Activate =
            new("Activate the selected remote");

        private readonly TranslationString _btnToggleStateTooltip_Deactivate =
            new(@"Deactivate the selected remote.
Inactive remote is completely invisible to git.");

        private readonly TranslationString _lvgEnabledHeader =
            new("Active");

        private readonly TranslationString _lvgDisabledHeader =
            new("Inactive");

        private readonly TranslationString _enabledRemoteAlreadyExists =
            new("An active remote named \"{0}\" already exists.");

        private readonly TranslationString _disabledRemoteAlreadyExists =
            new("An inactive remote named \"{0}\" already exists.");
        private IList<Repository> _repositoryHistory;
        #endregion

        public FormRemotes(IGitUICommands commands)
            : base(commands)
        {
            InitializeComponent();
            InitializeComplete();

            btnRemoteColor.BackColor = Color.Transparent;

            // Persist the text of the buttons to be able to restore it whenever the color is reset
            btnRemoteColor.Tag = btnRemoteColor.Text;

            // Set the minimum height of the color button after initialization
            btnRemoteColor.MinimumSize = new Size(DpiUtil.Scale(60), btnRemoteColor.Height);

            // remove text from 'new' and 'delete' buttons because now they are represented by icons
            New.Text = string.Empty;
            Delete.Text = string.Empty;
            toolTip1.SetToolTip(New, _btnNewTooltip.Text);
            toolTip1.SetToolTip(Delete, _btnDeleteTooltip.Text);

            _lvgEnabled = new ListViewGroup(_lvgEnabledHeader.Text, HorizontalAlignment.Left);
            _lvgDisabled = new ListViewGroup(_lvgDisabledHeader.Text, HorizontalAlignment.Left);
            Remotes.Groups.AddRange(new[] { _lvgEnabled, _lvgDisabled });

            Application.Idle += application_Idle;

            BranchName.DataPropertyName = nameof(IGitRef.LocalName);
            RemoteCombo.DataPropertyName = nameof(IGitRef.TrackingRemote);
            MergeWith.DataPropertyName = nameof(IGitRef.MergeWith);

            Remotes.Columns[0].Width = DpiUtil.Scale(120);
        }

        /// <summary>
        /// If this is not null before showing the dialog the given
        /// remote name will be preselected in the listbox.
        /// </summary>
        /// <remarks>exclusive of <see cref="PreselectLocalOnLoad"/>.</remarks>
        public string? PreselectRemoteOnLoad { get; set; }

        /// <summary>
        /// If this is not null before showing the dialog tab "Default push behavior" is opened
        /// and the given local name will be preselected in the listbox.
        /// </summary>
        /// <remarks>exclusive of <see cref="PreselectRemoteOnLoad"/>.</remarks>
        public string? PreselectLocalOnLoad { get; set; }

        /// <summary>
        /// Gets the list of remotes configured in .git/config file.
        /// </summary>
        private List<ConfigFileRemote>? UserGitRemotes { get; set; }

        private void Url_Enter(object sender, EventArgs e)
            => FillWithSomeGeneratedRemoteUrls(Url, r => r.Url);

        private void ComboBoxPushUrl_Enter(object sender, EventArgs e)
            => FillWithSomeGeneratedRemoteUrls(comboBoxPushUrl, r => r.PushUrl);

        private void BindRemotes(string? preselectRemote)
        {
            // we need to unwire and rewire the events to avoid excessive flickering
            Remotes.SelectedIndexChanged -= Remotes_SelectedIndexChanged;
            Remotes.Items.Clear();
            Remotes.Items.AddRange(UserGitRemotes.Select(remote =>
            {
                ListViewGroup group = remote.Disabled ? _lvgDisabled : _lvgEnabled;
                Color color = remote.Disabled ? SystemColors.GrayText : SystemColors.WindowText;
                return new ListViewItem(group) { Text = remote.Name, Tag = remote, ForeColor = color };
            }).ToArray());
            Remotes.SelectedIndexChanged += Remotes_SelectedIndexChanged;

            Remotes.SelectedIndices.Clear();
            if (UserGitRemotes.Any())
            {
                if (!string.IsNullOrEmpty(preselectRemote))
                {
                    ListViewItem lvi = Remotes.Items.Cast<ListViewItem>().FirstOrDefault(x => x.Text == preselectRemote);
                    if (lvi is not null)
                    {
                        lvi.Selected = true;
                        flpnlRemoteManagement.Enabled = !((ConfigFileRemote)lvi.Tag).Disabled;
                    }
                }

                // default fallback - if the preselection didn't work select the first available one
                if (Remotes.SelectedIndices.Count < 1)
                {
                    ListViewGroup group = _lvgEnabled.Items.Count > 0 ? _lvgEnabled : _lvgDisabled;
                    group.Items[0].Selected = true;
                }

                Remotes.FocusedItem = Remotes.SelectedItems[0];
                Remotes.Select();
            }
            else
            {
                RemoteName.Focus();
            }
        }

        private void BindBtnToggleState(bool disabled)
        {
            if (disabled)
            {
                btnToggleState.Image = DpiUtil.Scale(Images.EyeOpened.AdaptLightness());
                toolTip1.SetToolTip(btnToggleState, (_btnToggleStateTooltip_Activate.Text ?? "").Trim());
            }
            else
            {
                btnToggleState.Image = DpiUtil.Scale(Images.EyeClosed.AdaptLightness());
                toolTip1.SetToolTip(btnToggleState, (_btnToggleStateTooltip_Deactivate.Text ?? "").Trim());
            }
        }

        private IGitRef? GetHeadForSelectedRemoteBranch()
        {
            if (RemoteBranches.SelectedRows.Count != 1)
            {
                return null;
            }

            IGitRef head = RemoteBranches.SelectedRows[0].DataBoundItem as IGitRef;
            return head;
        }

        private void Initialize(string? preselectRemote = null, string? preselectLocal = null)
        {
            Validates.NotNull(_remotesManager);

            // refresh registered git remotes
            UserGitRemotes = _remotesManager.LoadRemotes(true).ToList();

            InitialiseTabRemotes(preselectRemote);

            if (preselectLocal is not null && UserGitRemotes.Count != 0)
            {
                ActivateTabDefaultPullBehaviors();
            }

            InitialiseTabDefaultPullBehaviors(preselectLocal);
        }

        private void ActivateTabDefaultPullBehaviors()
        {
            tabControl1.SelectedTab = tabPage2;
        }

        private void InitialiseTabRemotes(string? preselectRemote = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IList<Repository> repositoryHistory = ThreadHelper.JoinableTaskFactory.Run(RepositoryHistoryManager.Remotes.LoadRecentHistoryAsync);
            try
            {
                // because the binding the same BindingList to multiple controls,
                // and changes in one of the bound control automatically get reflected
                // in the other control, which causes rather frustrating UX.
                // to address that, re-create binding lists for each individual control

                // to stop the flicker binding the lists and
                // when the selected remote is getting reset and then selected again
                Url.BeginUpdate();
                comboBoxPushUrl.BeginUpdate();
                Remotes.BeginUpdate();

                _repositoryHistory = repositoryHistory;
                Url.DataSource = repositoryHistory.ToList();
                Url.DisplayMember = nameof(Repository.Path);
                Url.SelectedItem = null;
                Url.ResizeDropDownWidth();

                comboBoxPushUrl.DataSource = repositoryHistory.ToList();
                comboBoxPushUrl.DisplayMember = nameof(Repository.Path);
                comboBoxPushUrl.SelectedItem = null;
                comboBoxPushUrl.DropDownWidth = Url.DropDownWidth;

                BindRemotes(preselectRemote);
            }
            finally
            {
                Remotes.EndUpdate();
                if (Remotes.SelectedIndices.Count > 0)
                {
                    Remotes.EnsureVisible(Remotes.SelectedIndices[0]);
                }

                Url.EndUpdate();
                comboBoxPushUrl.EndUpdate();
            }
        }

        private void InitialiseTabDefaultPullBehaviors(string? preselectLocal = null)
        {
            List<IGitRef> heads = Module.GetRefs(RefsFilter.Heads).OrderBy(r => r.LocalName).ToList();
            SortableGitRefList headsList = new();
            headsList.AddRange(heads);

            RemoteRepositoryCombo.Sorted = false;
            RemoteRepositoryCombo.DataSource = new[] { new ConfigFileRemote() }.Union(UserGitRemotes).ToList();
            RemoteRepositoryCombo.DisplayMember = nameof(ConfigFileRemote.Name);

            RemoteBranches.AutoGenerateColumns = false;
            RemoteBranches.SelectionChanged -= RemoteBranchesSelectionChanged;
            RemoteBranches.DataError += RemoteBranchesDataError;
            RemoteBranches.DataSource = headsList;
            RemoteBranches.ClearSelection();
            RemoteBranches.SelectionChanged += RemoteBranchesSelectionChanged;
            DataGridViewRow preselectLocalRow = RemoteBranches.Rows.Cast<DataGridViewRow>().
                FirstOrDefault(r => r.DataBoundItem is IGitRef gitRef ? gitRef.LocalName == preselectLocal : false);
            if (preselectLocalRow is not null)
            {
                preselectLocalRow.Selected = true;
            }
            else if (RemoteBranches.Rows.Count > 0)
            {
                RemoteBranches.Rows[0].Selected = true;
            }
        }

        private void SetRemoteColor(Color color)
        {
            if (color == Color.Transparent)
            {
                btnRemoteColor.Text = (string)btnRemoteColor.Tag;
                btnRemoteColor.BackColor = Color.Transparent;

                btnRemoteColorReset.Visible = false;
            }
            else
            {
                if (btnRemoteColor.BackColor == Color.Transparent)
                {
                    btnRemoteColor.Text = string.Empty;
                    btnRemoteColorReset.Visible = true;
                }

                btnRemoteColor.BackColor = color;
            }
        }

        private void application_Idle(object sender, EventArgs e)
        {
            // we need this event only once, so unwire
            Application.Idle -= application_Idle;

            // make sure only single load option is given
            if (PreselectRemoteOnLoad is not null && PreselectLocalOnLoad is not null)
            {
                throw new ArgumentException($"Only one option allowed:" +
                    $" Either {nameof(PreselectRemoteOnLoad)} or {nameof(PreselectLocalOnLoad)}");
            }

            pnlMgtPuttySsh.Visible = GitSshHelpers.IsPlink;

            if (!AppSettings.AlwaysShowAdvOpt)
            {
                lblRemoteColor.Visible = false;
                flpnlRemoteColors.Visible = false;
            }

            // if Putty SSH isn't enabled, reduce the minimum height of the form
            MinimumSize = new Size(MinimumSize.Width, pnlMgtPuttySsh.Visible ? MinimumSize.Height : MinimumSize.Height - pnlMgtPuttySsh.Height);
            Height = MinimumSize.Height + DpiUtil.Scale(36);

            // adjust width of the labels if required
            // this may be necessary if the translated labels require more space than English versions
            // the longest label is likely to be label3 (Private key file), so use it as a guide
            Size widestLabelMinSize = new(label3.Width, 0);
            label1.MinimumSize = label1.MaximumSize = widestLabelMinSize;        // Name
            label2.MinimumSize = label2.MaximumSize = widestLabelMinSize;        // Url
            labelPushUrl.MinimumSize = labelPushUrl.MaximumSize = widestLabelMinSize;  // Push URL

            _remotesManager = new ConfigFileRemoteSettingsManager(() => Module);

            // load the data for the very first time
            Initialize(PreselectRemoteOnLoad, PreselectLocalOnLoad);
        }

        private void btnRemoteColor_Click(object sender, EventArgs e)
        {
            colorDialog.Color = btnRemoteColor.BackColor == Color.Transparent
                ? AppColor.RemoteBranch.GetThemeColor()
                : btnRemoteColor.BackColor;

            if (colorDialog.ShowDialog() == DialogResult.OK)
            {
                SetRemoteColor(colorDialog.Color);
            }
        }

        private void btnRemoteColorReset_Click(object sender, EventArgs e)
        {
            SetRemoteColor(Color.Transparent);
        }

        private void btnToggleState_Click(object sender, EventArgs e)
        {
            if (_selectedRemote is null)
            {
                btnToggleState.Visible = false;
                return;
            }

            Validates.NotNull(_remotesManager);

            _selectedRemote.Disabled = !_selectedRemote.Disabled;

            Validates.NotNull(_selectedRemote.Name);

            _remotesManager.ToggleRemoteState(_selectedRemote.Name, _selectedRemote.Disabled);

            Initialize(_selectedRemote.Name);
        }

        private bool ValidateRemoteDoesNotExist(string remote)
        {
            Validates.NotNull(_remotesManager);

            if (_remotesManager.EnabledRemoteExists(remote))
            {
                MessageBox.Show(this, string.Format(_enabledRemoteAlreadyExists.Text, remote), _gitMessage.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);

                return false;
            }

            if (_remotesManager.DisabledRemoteExists(remote))
            {
                MessageBox.Show(this, string.Format(_disabledRemoteAlreadyExists.Text, remote), _gitMessage.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);

                return false;
            }

            return true;
        }

        private void SaveClick(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(RemoteName.Text))
            {
                return;
            }

            string remote = RemoteName.Text.Trim();
            string remoteUrl = Url.Text.Trim();
            string remotePushUrl = comboBoxPushUrl.Text.Trim();
            bool creatingNew = _selectedRemote is null;

            string? color = null;
            if (btnRemoteColor.BackColor != Color.Transparent)
            {
                color = ColorTranslator.ToHtml(btnRemoteColor.BackColor);
            }

            try
            {
                // disable the control while saving
                tabControl1.Enabled = false;

                if ((string.IsNullOrEmpty(remotePushUrl) && checkBoxSepPushUrl.Checked) ||
                    (!string.IsNullOrEmpty(remotePushUrl) && remotePushUrl.Equals(remoteUrl, StringComparison.OrdinalIgnoreCase)))
                {
                    checkBoxSepPushUrl.Checked = false;
                }

                if (creatingNew && !ValidateRemoteDoesNotExist(remote))
                {
                    return;
                }

                Validates.NotNull(_remotesManager);

                // update all other remote properties
                ConfigFileRemoteSaveResult result = _remotesManager.SaveRemote(_selectedRemote,
                                                       remote,
                                                       remoteUrl,
                                                       checkBoxSepPushUrl.Checked ? remotePushUrl : null,
                                                       PuttySshKey.Text,
                                                       color);

                if (!string.IsNullOrEmpty(result.UserMessage))
                {
                    MessageBox.Show(this, result.UserMessage, _gitMessage.Text, MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    return;
                }
                else
                {
                    // This will cause the module's remotes colors to reload
                    Module.ResetRemoteColors();

                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        IList<Repository> repositoryHistory = await RepositoryHistoryManager.Remotes.LoadRecentHistoryAsync();

                        await this.SwitchToMainThreadAsync();
                        _formRemotesController.RemoteUpdate(repositoryHistory, _selectedRemote?.Url, remoteUrl);
                        if (checkBoxSepPushUrl.Checked)
                        {
                            _formRemotesController.RemoteUpdate(repositoryHistory, _selectedRemote?.PushUrl, remotePushUrl);
                        }

                        await RepositoryHistoryManager.Remotes.SaveRecentHistoryAsync(repositoryHistory);
                    });
                }

                // if the user has just created a fresh new remote
                // there may be a need to configure it
                if (result.ShouldUpdateRemote &&
                    !string.IsNullOrEmpty(remoteUrl) &&
                    MessageBox.Show(this,
                        _questionAutoPullBehaviour.Text,
                        _questionAutoPullBehaviourCaption.Text,
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    UICommands.StartPullDialogAndPullImmediately(
                        remote: remote,
                        pullAction: GitPullAction.Fetch);
                    _remotesManager.ConfigureRemotes(remote);
                    UICommands.RepoChangedNotifier.Notify();
                }
            }
            finally
            {
                // re-enable the control and re-initialize
                tabControl1.Enabled = true;
                Initialize(remote);
            }
        }

        private void NewClick(object sender, EventArgs e)
        {
            Remotes.SelectedIndices.Clear();
            RemoteName.Focus();
        }

        private void DeleteClick(object sender, EventArgs e)
        {
            if (_selectedRemote is null)
            {
                return;
            }

            if (MessageBox.Show(this,
                                _questionDeleteRemote.Text,
                                _questionDeleteRemoteCaption.Text,
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Warning,
                                MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                Validates.NotNull(_remotesManager);

                string output = _remotesManager.RemoveRemote(_selectedRemote);
                if (!string.IsNullOrEmpty(output))
                {
                    MessageBox.Show(this, output, _gitMessage.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                // Deleting a remote from the history list may be undesirable as
                // it would hinder user's ability to *quickly* clone the remote repository
                // The flipside is that the history list may grow long without a UI to manage it

                Initialize();
            }
        }

        private void SshBrowseClick(object sender, EventArgs e)
        {
            using OpenFileDialog dialog = new()
            {
                Filter = _sshKeyOpenFilter.Text + @"|*.ppk",
                InitialDirectory = ".",
                Title = _sshKeyOpenCaption.Text
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                PuttySshKey.Text = dialog.FileName;
            }
        }

        private void LoadSshKeyClick(object sender, EventArgs e)
        {
            PuttyHelpers.StartPageantIfConfigured(() => PuttySshKey.Text);
        }

        private void TestConnectionClick(object sender, EventArgs e)
        {
            string url = Url.Text;
            ThreadHelper.FileAndForget(() => new Plink().ConnectAsync(url));
        }

        private void RemoteBranchesDataError(object sender, DataGridViewDataErrorEventArgs e)
        {
            MessageBox.Show(this,
                            string.Format(_remoteBranchDataError.Text, RemoteBranches.Rows[e.RowIndex].Cells[0].Value, RemoteBranches.Columns[e.ColumnIndex].HeaderText),
                            TranslatedStrings.Error, MessageBoxButtons.OK, MessageBoxIcon.Error);
            RemoteBranches.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = "";
        }

        private void RemoteBranchesSelectionChanged(object sender, EventArgs e)
        {
            IGitRef head = GetHeadForSelectedRemoteBranch();
            if (head is null)
            {
                return;
            }

            LocalBranchNameEdit.Text = head.Name;
            LocalBranchNameEdit.ReadOnly = true;
            RemoteRepositoryCombo.SelectedItem = UserGitRemotes.FirstOrDefault(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, head.TrackingRemote));
            if (RemoteRepositoryCombo.SelectedItem is null)
            {
                RemoteRepositoryCombo.SelectedIndex = 0;
            }

            DefaultMergeWithCombo.Text = head.MergeWith;
        }

        private void DefaultMergeWithComboDropDown(object sender, EventArgs e)
        {
            IGitRef head = GetHeadForSelectedRemoteBranch();
            if (head is null)
            {
                return;
            }

            DefaultMergeWithCombo.Items.Clear();
            DefaultMergeWithCombo.Items.Add("");

            string currentSelectedRemote = RemoteRepositoryCombo.Text.Trim();

            if (string.IsNullOrEmpty(head.TrackingRemote) || string.IsNullOrEmpty(currentSelectedRemote))
            {
                return;
            }

            string remoteUrl = Module.GetSetting(string.Format(SettingKeyString.RemoteUrl, currentSelectedRemote));
            if (string.IsNullOrEmpty(remoteUrl))
            {
                return;
            }

            foreach (IGitRef remoteHead in Module.GetRefs(RefsFilter.Remotes))
            {
                if (remoteHead.Name.ToLower().Contains(currentSelectedRemote.ToLower()))
                {
                    DefaultMergeWithCombo.Items.Add(remoteHead.LocalName);
                }
            }
        }

        private void RemoteRepositoryComboValidated(object sender, EventArgs e)
        {
            IGitRef head = GetHeadForSelectedRemoteBranch();
            if (head is null)
            {
                return;
            }

            head.TrackingRemote = RemoteRepositoryCombo.Text;
        }

        private void DefaultMergeWithComboValidated(object sender, EventArgs e)
        {
            IGitRef head = GetHeadForSelectedRemoteBranch();
            if (head is null)
            {
                return;
            }

            head.MergeWith = DefaultMergeWithCombo.Text;
        }

        private void SaveDefaultPushPullClick(object sender, EventArgs e)
        {
            Initialize();
        }

        private void RemoteName_TextChanged(object sender, EventArgs e)
        {
            Save.Enabled = RemoteName.Text.Trim().Length > 0;
        }

        private void Remotes_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (Remotes.SelectedIndices.Count > 0 && _selectedRemote == Remotes.SelectedItems[0].Tag)
            {
                return;
            }

            New.Enabled = Delete.Enabled = btnToggleState.Enabled = false;
            RemoteName.Text = string.Empty;
            Url.Text = string.Empty;
            comboBoxPushUrl.Text = string.Empty;
            checkBoxSepPushUrl.Checked = false;
            PuttySshKey.Text = string.Empty;
            gbMgtPanel.Text = _gbMgtPanelHeaderNew.Text;

            if (Remotes.SelectedIndices.Count < 1)
            {
                // we are here because we're adding a new remote - so no remotes selected
                // we just need to enable the panel so the user can enter the information
                _selectedRemote = null;
                flpnlRemoteManagement.Enabled = true;
                return;
            }

            // reset all controls and disable all buttons until we have a selection
            _selectedRemote = Remotes.SelectedItems[0].Tag as ConfigFileRemote;
            if (_selectedRemote is null)
            {
                return;
            }

            New.Enabled = Delete.Enabled = btnToggleState.Enabled = true;
            RemoteName.Text = _selectedRemote.Name;
            Url.Text = _selectedRemote.Url;
            comboBoxPushUrl.Text = _selectedRemote.PushUrl;
            checkBoxSepPushUrl.Checked = !string.IsNullOrEmpty(_selectedRemote.PushUrl);
            PuttySshKey.Text = _selectedRemote.PuttySshKey;
            gbMgtPanel.Text = _gbMgtPanelHeaderEdit.Text;
            BindBtnToggleState(_selectedRemote.Disabled);
            btnToggleState.Visible = true;
            flpnlRemoteManagement.Enabled = !_selectedRemote.Disabled;

            if (string.IsNullOrWhiteSpace(_selectedRemote.Color))
            {
                SetRemoteColor(Color.Transparent);
            }
            else
            {
                SetRemoteColor(ColorTranslator.FromHtml(_selectedRemote.Color));
            }
        }

        private void checkBoxSepPushUrl_CheckedChanged(object sender, EventArgs e)
        {
            ShowSeparatePushUrl(checkBoxSepPushUrl.Checked);
        }

        private void Remotes_MouseUp(object sender, MouseEventArgs e)
        {
            flpnlRemoteManagement.Enabled = !_selectedRemote?.Disabled ?? true;
        }

        private void ShowSeparatePushUrl(bool visible)
        {
            labelPushUrl.Visible = visible;
            comboBoxPushUrl.Visible = visible;
            folderBrowserButtonPushUrl.Visible = visible;

            label2.Text = visible
                ? _labelUrlAsFetch.Text
                : _labelUrlAsFetchPush.Text;
        }

        private void FillWithSomeGeneratedRemoteUrls(CaseSensitiveComboBox combobox, Func<ConfigFileRemote, string> urlGetter)
        {
            string remoteName = RemoteName.Text;
            bool fillEmptyUrl = true;

            if (string.IsNullOrWhiteSpace(RemoteName.Text) || _genericRemotesNames.Contains(RemoteName.Text))
            {
                remoteName = "TO_REPLACE";
                fillEmptyUrl = false;
            }

            if (UserGitRemotes?.Count != 0)
            {
                HashSet<string> candidates = new(UserGitRemotes.Count);

                // TODO: Same thing for AzureDevOpsRemoteParser (that doesn't have the same url format!) ???
                GitHostingRemoteParser gitHostingRemoteParser = new();
                foreach (ConfigFileRemote remote in UserGitRemotes)
                {
                    string url = urlGetter(remote);

                    if (string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    // Simple replace tentative
                    if (url.Contains(remote.Name))
                    {
                        candidates.Add(urlGetter(remote).Replace($"{remote.Name}/", $"{remoteName}/"));
                    }

                    // Extract from "known" git hosting pattern
                    if (gitHostingRemoteParser.TryExtractGitHostingDataFromRemoteUrl(remote.Url, out _, out string owner, out _))
                    {
                        candidates.Add(url.Replace($"{owner}/", $"{remoteName}/"));
                    }
                }

                if (candidates.Count > 0)
                {
                    string previousValues = combobox.Text;
                    IList<Repository> proposedRepositories = _repositoryHistory.ToList();
                    bool added = false;
                    foreach (string url in candidates)
                    {
                        if (!proposedRepositories.Any(r => r.Path == url))
                        {
                            added = true;
                            proposedRepositories.Insert(0, new Repository(url));
                        }
                    }

                    if (added)
                    {
                        // if there is a previous value, keep it
                        if (!string.IsNullOrEmpty(previousValues))
                        {
                            proposedRepositories.Insert(0, new Repository(previousValues));
                        }

                        combobox.DataSource = proposedRepositories;

                        // Don't auto select a value when generic remote name entered or more than 1 result added.
                        if (string.IsNullOrEmpty(previousValues) && (!fillEmptyUrl || candidates.Count > 1))
                        {
                            combobox.Text = string.Empty;
                        }
                    }
                }
            }
        }

        private void RemoteName_Enter(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(RemoteName.Text) || string.IsNullOrEmpty(Url.Text))
            {
                return;
            }

            GitHostingRemoteParser gitHostingRemoteParser = new();
            if (gitHostingRemoteParser.TryExtractGitHostingDataFromRemoteUrl(Url.Text, out _, out string owner, out _))
            {
                RemoteName.Text = owner;
                RemoteName.SelectAll();
            }
        }
    }
}
