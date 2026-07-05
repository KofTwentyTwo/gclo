using System;
using gclo.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace gclo
{
    /// <summary>
    /// Four-step account wizard (identity, token, organization, destination) over an
    /// <see cref="AccountWizardViewModel"/>. Next/Save and Back are the dialog's primary
    /// and secondary buttons; both clicks are intercepted (deferral + <c>args.Cancel</c>)
    /// so the dialog only closes on Cancel or after the final step saved successfully —
    /// <see cref="Saved"/> tells the caller which of the two happened.
    ///
    /// As with every ContentDialog, the caller must set <c>XamlRoot</c> before
    /// <c>ShowAsync</c>.
    /// </summary>
    public sealed partial class AccountWizardDialog : ContentDialog
    {
        // A ContentDialog has no HWND of its own; the host window supplies one for pickers.
        private readonly Func<nint> _windowHandleProvider;

        /// <summary>Guards Next/Back against double activation while a step handler runs.</summary>
        private bool _interactionInFlight;

        /// <summary>The wizard state this dialog renders; target of every x:Bind.</summary>
        public AccountWizardViewModel ViewModel { get; }

        /// <summary>True once the account was saved; false when the dialog was dismissed.</summary>
        public bool Saved { get; private set; }

        /// <summary>
        /// Wires the dialog to its (caller-owned) view model. The
        /// <paramref name="windowHandleProvider"/> returns the host window's HWND,
        /// needed to initialize the folder picker on the destination step.
        /// </summary>
        public AccountWizardDialog(AccountWizardViewModel viewModel, Func<nint> windowHandleProvider)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            ArgumentNullException.ThrowIfNull(windowHandleProvider);
            ViewModel = viewModel;
            _windowHandleProvider = windowHandleProvider;
            InitializeComponent();

            // PasswordBox has no reliable two-way binding: seed it with the token in
            // effect (editing an account), and mirror edits back by hand. The seed's
            // PasswordChanged echo writes the same value back, a no-op set.
            if (ViewModel.Token.Length > 0)
            {
                TokenBox.Password = ViewModel.Token;
            }

            // NumberBox.Value is a double; the int is mirrored by hand (see ValueChanged).
            ConcurrencyBox.Value = ViewModel.MaxConcurrency;
        }

        // ---------------------------------------------------------------- x:Bind helpers

        /// <summary>Shows a step panel only while it is the wizard's current step.</summary>
        public Visibility StepVisible(int step, int current)
            => step == current ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Primary button caption: "Save" on the last step, "Next" before it.</summary>
        public string PrimaryButtonLabel(int step) => step == 4 ? "Save" : "Next";

        /// <summary>Back is available everywhere but the first step.</summary>
        public bool BackEnabled(int step) => step > 1;

        /// <summary>Next is locked while the token step is talking to GitHub.</summary>
        public bool PrimaryEnabled(bool isValidatingToken) => !isValidatingToken;

        /// <summary>
        /// Label for the org-subfolder checkbox; names the actual organization once one is chosen.
        /// </summary>
        public string OrgSubfolderLabel(string organization)
            => string.IsNullOrWhiteSpace(organization)
                ? "Create org subfolder"
                : $"Create {organization.Trim()} subfolder";

        // ---------------------------------------------------------------- buttons

        /// <summary>
        /// Next/Save: advances one validated step, or saves and closes on the last one.
        /// The dialog stays open (<c>args.Cancel</c>) in every case except a successful save.
        /// </summary>
        private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_interactionInFlight)
            {
                args.Cancel = true;
                return;
            }

            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            _interactionInFlight = true;
            try
            {
                // TryAdvanceAsync returns true on the last step without advancing;
                // capture where we were to tell "advanced" from "ready to save".
                bool wasLastStep = ViewModel.IsLastStep;
                if (!await ViewModel.TryAdvanceAsync())
                {
                    args.Cancel = true;
                    AnnounceStepError();
                    return;
                }

                if (!wasLastStep)
                {
                    args.Cancel = true; // moved to the next step; keep the dialog open
                    return;
                }

                try
                {
                    await ViewModel.SaveAsync();
                    Saved = true; // not canceled: the dialog closes now
                }
                catch (Exception ex)
                {
                    // Duplicate name raced in, disk write failed, vault rejected the
                    // token, ... — surface it and let the user adjust or cancel.
                    args.Cancel = true;
                    SaveErrorBar.Message = ex.Message;
                    SaveErrorBar.IsOpen = true;
                }
            }
            finally
            {
                _interactionInFlight = false;
                deferral.Complete();
            }
        }

        /// <summary>Back: never closes the dialog; steps the wizard back one page.</summary>
        private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            if (!_interactionInFlight)
            {
                ViewModel.GoBack();
            }
        }

        // ---------------------------------------------------------------- inputs

        // PasswordBox does not support reliable two-way x:Bind on Password;
        // mirror it into the view model by hand.
        private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
            => ViewModel.Token = ((PasswordBox)sender).Password;

        // An editable ComboBox does not render Text set before its template loaded;
        // when editing an account, step 3 would show blank despite the seeded
        // organization — see EditableComboBox.
        private void OrgBox_Loaded(object sender, RoutedEventArgs e)
            => EditableComboBox.ReapplyText((ComboBox)sender, ViewModel.Organization);

        /// <summary>Mirrors the NumberBox's double into the view model's int.</summary>
        private void ConcurrencyBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (double.IsNaN(sender.Value))
            {
                // The box was cleared: push the canonical value back so it redisplays.
                sender.Value = ViewModel.MaxConcurrency;
                return;
            }

            ViewModel.MaxConcurrency = (int)Math.Clamp(
                Math.Round(sender.Value), AppSettings.MinConcurrency, AppSettings.MaxConcurrency);
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*"); // required in packaged apps

            WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandleProvider());

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                ViewModel.TargetRoot = folder.Path;
            }
        }

        // ---------------------------------------------------------------- announcements

        /// <summary>
        /// Raises LiveRegionChanged on the current step's error text after a failed
        /// advance — LiveSetting alone does not announce: XAML never raises the event
        /// automatically. Enqueued so the binding has pushed the new text first.
        /// </summary>
        private void AnnounceStepError()
        {
            TextBlock? errorText = ViewModel.Step switch
            {
                1 => NameErrorText,
                2 => TokenErrorText,
                _ => null, // steps 3 and 4 have no message property; the fields show what is required
            };
            if (errorText is null)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(errorText)
                    ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(errorText);
                peer?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
            });
        }
    }
}
