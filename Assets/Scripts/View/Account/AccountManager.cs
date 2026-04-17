using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Manages the account forms within the settings screen: display name (works offline),
/// login, register, verification, forgot/reset password, email change, and password change.
/// </summary>
public class AccountManager
{
    private readonly ApiClient _api;

    /// <summary>
    /// Fired when the visible account form changes (e.g. login → change email).
    /// Parameter is the element that should receive initial keyboard focus.
    /// </summary>
    public event System.Action<UnityEngine.UIElements.VisualElement> FormChanged;

    // Forms
    private readonly VisualElement _loginForm;
    private readonly VisualElement _verifyForm;
    private readonly VisualElement _forgotForm;
    private readonly VisualElement _resetForm;
    private readonly VisualElement _accountInfo;
    private readonly VisualElement _changeEmailForm;
    private readonly VisualElement _confirmEmailForm;
    private readonly VisualElement _changePasswordForm;

    // Display name (always visible, works offline)
    private readonly EditableLabel _displayName;
    private readonly Label _displayNameError;

    // Login / register fields (shared form)
    private readonly LabeledField _loginEmail;
    private readonly LabeledField _loginPassword;
    private readonly Label _loginError;

    // Verify fields
    private readonly Label _verifyMessage;
    private readonly LabeledField _verifyCode;
    private readonly Label _verifyError;
    private readonly Label _verifySuccess;

    // Forgot password fields
    private readonly LabeledField _forgotEmail;
    private readonly Label _forgotError;
    private readonly Label _forgotSuccess;

    // Reset password fields
    private readonly Label _resetMessage;
    private readonly LabeledField _resetCode;
    private readonly LabeledField _resetNewPassword;
    private readonly LabeledField _resetConfirmPassword;
    private readonly Label _resetError;
    private readonly Label _resetSuccess;

    // Account status label ("Playing offline" / "Logged in as ...")
    private readonly Label _accountStatusLabel;

    // Account info fields
    private readonly Label _accountEmail;
    private readonly Label _accountError;

    // Change email fields
    private readonly LabeledField _newEmail;
    private readonly LabeledField _confirmPassword;
    private readonly Label _changeEmailError;

    // Confirm email change fields
    private readonly Label _confirmEmailMessage;
    private readonly LabeledField _confirmEmailCode;
    private readonly Label _confirmEmailError;

    // Change password fields
    private readonly LabeledField _currentPassword;
    private readonly LabeledField _newPassword;
    private readonly LabeledField _confirmNewPassword;
    private readonly Label _changePasswordError;
    private readonly Label _changePasswordSuccess;

    // Track email for resend / confirm flows
    private string _pendingVerificationEmail;
    private string _pendingNewEmail;
    private string _pendingResetEmail;

    // When set, the verify form is completing a new-device OTP challenge
    // (Phase 1B) and OnVerifyCode should call VerifyDeviceAsync instead of
    // the post-registration VerifyCodeAsync. The password is retained in
    // memory only until the OTP is entered.
    private string _pendingDeviceVerificationPassword;

    private readonly ConfirmModal _logoutModal;

    // Co-op color picker (visible in account-info form when logged in)
    private readonly CoopColorPicker _colorPicker;

    public AccountManager(VisualElement settingsRoot, VisualElement logoutModalRoot)
    {
        _api = new ApiClient();

        // Logout confirmation modal
        _logoutModal = new ConfirmModal(
            logoutModalRoot,
            "Log out?",
            "Log Out",
            "Cancel",
            isDanger: true
        );
        _logoutModal.Confirmed += OnLogoutConfirm;
        _logoutModal.Cancelled += () => _logoutModal.Hide();

        // Co-op color picker — visible in account-info form
        _colorPicker = new CoopColorPicker();
        var colorSlot = settingsRoot.Q("coop-color-slot");
        if (colorSlot != null)
            colorSlot.Add(_colorPicker.Root);

        _colorPicker.OnPreview += OnCoopColorPreview;
        _colorPicker.OnCommit += OnCoopColorCommit;

        // Display name — always visible, works offline
        _displayName = new EditableLabel();
        _displayName.OnCommit += OnDisplayNameCommit;
        _displayNameError = settingsRoot.Q<Label>("display-name-error");
        settingsRoot.Q("display-name-slot").Add(_displayName.Root);

        // Load persisted display name into the shared static on first access
        if (string.IsNullOrEmpty(GameSettings.DisplayName))
            GameSettings.DisplayName = UnityEngine.PlayerPrefs.GetString(
                GameSettings.DisplayNamePrefKey,
                ""
            );
        _displayName.Value = string.IsNullOrEmpty(GameSettings.DisplayName)
            ? "Player"
            : GameSettings.DisplayName;

        // Apply initial color to the display name text
        var localColor = PlayerPrefs.GetString("LocalCoopColor", "");
        if (!string.IsNullOrEmpty(localColor))
        {
            var initColor = _colorPicker.SetValueWithoutNotify(localColor);
            ApplyColorToDisplayName(initColor);
        }

        // Combined login / register form
        _loginForm = settingsRoot.Q("login-form");
        _loginEmail = new LabeledField("Email", "login-email") { OnSubmit = OnLogin };
        _loginPassword = new LabeledField("Password", "login-password")
        {
            IsPassword = true,
            OnSubmit = OnLogin,
        };
        _loginError = settingsRoot.Q<Label>("login-error");

        // "Forgot password?" inline in the password label row
        var forgotBtn = new Button(ShowForgotForm) { text = "Forgot password?" };
        forgotBtn.AddToClassList("labeled-field__label-action");
        _loginPassword.AddToLabelRow(forgotBtn);

        var loginFields = settingsRoot.Q("login-fields");
        loginFields.Add(_loginEmail.Root);
        loginFields.Add(_loginPassword.Root);

        settingsRoot.Q<Button>("login-submit-btn").clicked += OnLogin;
        settingsRoot.Q<Button>("register-submit-btn").clicked += OnRegister;

        // Verify form
        _verifyForm = settingsRoot.Q("verify-form");
        _verifyMessage = settingsRoot.Q<Label>("verify-message");
        _verifyCode = new LabeledField("Verification Code", "verify-code")
        {
            OnSubmit = OnVerifyCode,
        };
        _verifyError = settingsRoot.Q<Label>("verify-error");
        _verifySuccess = settingsRoot.Q<Label>("verify-success");

        settingsRoot.Q("verify-fields").Add(_verifyCode.Root);

        settingsRoot.Q<Button>("verify-submit-btn").clicked += OnVerifyCode;
        settingsRoot.Q<Button>("resend-verify-btn").clicked += OnResendVerification;
        settingsRoot.Q<Button>("verify-back-btn").clicked += ShowLoginForm;

        // Forgot password form
        _forgotForm = settingsRoot.Q("forgot-form");
        _forgotEmail = new LabeledField("Email", "forgot-email") { OnSubmit = OnForgotPassword };
        _forgotError = settingsRoot.Q<Label>("forgot-error");
        _forgotSuccess = settingsRoot.Q<Label>("forgot-success");

        settingsRoot.Q("forgot-fields").Add(_forgotEmail.Root);

        settingsRoot.Q<Button>("forgot-submit-btn").clicked += OnForgotPassword;
        settingsRoot.Q<Button>("forgot-back-btn").clicked += ShowLoginForm;

        // Reset password form
        _resetForm = settingsRoot.Q("reset-form");
        _resetMessage = settingsRoot.Q<Label>("reset-message");
        _resetCode = new LabeledField("Reset Code", "reset-code") { OnSubmit = OnResetPassword };
        _resetNewPassword = new LabeledField("New Password", "reset-new-password")
        {
            IsPassword = true,
            OnSubmit = OnResetPassword,
        };
        _resetConfirmPassword = new LabeledField("Confirm Password", "reset-confirm-password")
        {
            IsPassword = true,
            OnSubmit = OnResetPassword,
        };
        _resetError = settingsRoot.Q<Label>("reset-error");
        _resetSuccess = settingsRoot.Q<Label>("reset-success");

        var resetFields = settingsRoot.Q("reset-fields");
        resetFields.Add(_resetCode.Root);
        resetFields.Add(_resetNewPassword.Root);
        resetFields.Add(_resetConfirmPassword.Root);

        settingsRoot.Q<Button>("reset-submit-btn").clicked += OnResetPassword;
        settingsRoot.Q<Button>("reset-back-btn").clicked += ShowLoginForm;

        // Account status label
        _accountStatusLabel = settingsRoot.Q<Label>("account-status-label");

        // Account info
        _accountInfo = settingsRoot.Q("account-info");
        _accountEmail = settingsRoot.Q<Label>("account-email");
        _accountError = settingsRoot.Q<Label>("account-error");

        settingsRoot.Q<Button>("change-email-btn").clicked += ShowChangeEmailForm;
        settingsRoot.Q<Button>("change-password-btn").clicked += ShowChangePasswordForm;
        settingsRoot.Q<Button>("logout-btn").clicked += () => _logoutModal.Show();

        // Change email form
        _changeEmailForm = settingsRoot.Q("change-email-form");
        _newEmail = new LabeledField("New Email", "new-email") { OnSubmit = OnChangeEmail };
        _confirmPassword = new LabeledField("Current Password", "confirm-password")
        {
            IsPassword = true,
            OnSubmit = OnChangeEmail,
        };
        _changeEmailError = settingsRoot.Q<Label>("change-email-error");

        var changeEmailFields = settingsRoot.Q("change-email-fields");
        changeEmailFields.Add(_newEmail.Root);
        changeEmailFields.Add(_confirmPassword.Root);

        settingsRoot.Q<Button>("change-email-submit-btn").clicked += OnChangeEmail;
        settingsRoot.Q<Button>("change-email-back-btn").clicked += () =>
            ShowAccountInfo("change-email-btn");

        // Confirm email change form
        _confirmEmailForm = settingsRoot.Q("confirm-email-form");
        _confirmEmailMessage = settingsRoot.Q<Label>("confirm-email-message");
        _confirmEmailCode = new LabeledField("Confirmation Code", "confirm-email-code")
        {
            OnSubmit = OnConfirmEmailChange,
        };
        _confirmEmailError = settingsRoot.Q<Label>("confirm-email-error");

        settingsRoot.Q("confirm-email-fields").Add(_confirmEmailCode.Root);

        settingsRoot.Q<Button>("confirm-email-submit-btn").clicked += OnConfirmEmailChange;
        settingsRoot.Q<Button>("confirm-email-back-btn").clicked += () =>
            ShowAccountInfo("change-email-btn");

        // Change password form
        _changePasswordForm = settingsRoot.Q("change-password-form");
        _currentPassword = new LabeledField("Current Password", "current-password")
        {
            IsPassword = true,
            OnSubmit = OnChangePassword,
        };
        _newPassword = new LabeledField("New Password", "new-password")
        {
            IsPassword = true,
            OnSubmit = OnChangePassword,
        };
        _confirmNewPassword = new LabeledField("Confirm New Password", "confirm-new-password")
        {
            IsPassword = true,
            OnSubmit = OnChangePassword,
        };
        _changePasswordError = settingsRoot.Q<Label>("change-password-error");
        _changePasswordSuccess = settingsRoot.Q<Label>("change-password-success");

        var changePasswordFields = settingsRoot.Q("change-password-fields");
        changePasswordFields.Add(_currentPassword.Root);
        changePasswordFields.Add(_newPassword.Root);
        changePasswordFields.Add(_confirmNewPassword.Root);

        settingsRoot.Q<Button>("change-password-submit-btn").clicked += OnChangePassword;
        settingsRoot.Q<Button>("change-password-back-btn").clicked += () =>
            ShowAccountInfo("change-password-btn");

        // Start in correct state
        if (_api.IsLoggedIn)
            ShowAccountInfo();
        else
            ShowLoginForm();
    }

    private void ShowLoginForm()
    {
        HideAllForms();
        _loginEmail.Value = "";
        _loginPassword.Value = "";
        _pendingDeviceVerificationPassword = null;
        SetVisible(_loginForm, true);
        UpdateStatusLabel();
        FormChanged?.Invoke(_loginEmail.Input);
    }

    private void ShowVerifyForm()
    {
        HideAllForms();
        _verifyCode.Value = "";
        SetVisible(_verifyForm, true);
        FormChanged?.Invoke(_verifyCode.Input);
    }

    private void ShowForgotForm()
    {
        HideAllForms();
        _forgotEmail.Value = "";
        SetVisible(_forgotForm, true);
        FormChanged?.Invoke(_forgotEmail.Input);
    }

    private void ShowResetForm()
    {
        HideAllForms();
        _resetCode.Value = "";
        _resetNewPassword.Value = "";
        _resetConfirmPassword.Value = "";
        _resetMessage.text = $"We sent a 6-digit code to {_pendingResetEmail}.";
        SetVisible(_resetForm, true);
        FormChanged?.Invoke(_resetCode.Input);
    }

    private async void ShowAccountInfo(string focusBtnName = "change-email-btn")
    {
        HideAllForms();
        SetVisible(_accountInfo, true);
        UpdateStatusLabel();
        FormChanged?.Invoke(_accountInfo.Q<UnityEngine.UIElements.Button>(focusBtnName));

        // Refresh account state from server
        var result = await _api.GetMeAsync();

        if (result.Success && !string.IsNullOrEmpty(_api.DisplayName))
        {
            GameSettings.DisplayName = _api.DisplayName;
            _displayName.Value = _api.DisplayName;
        }

        // Sync co-op color from the server response
        if (result.Success && result.Data != null && !string.IsNullOrEmpty(result.Data.coopColor))
        {
            var serverColor = _colorPicker.SetValueWithoutNotify(result.Data.coopColor);
            ApplyColorToDisplayName(serverColor);
            PlayerPrefs.SetString("LocalCoopColor", result.Data.coopColor);
        }

        _accountEmail.text = MaskEmail(_api.Email);
        UpdateStatusLabel();
    }

    private void UpdateStatusLabel()
    {
        if (_accountStatusLabel == null)
            return;
        _accountStatusLabel.text = _api.IsLoggedIn ? "Logged in as:" : "Playing offline as:";
    }

    private void ShowChangeEmailForm()
    {
        HideAllForms();
        _newEmail.Value = "";
        _confirmPassword.Value = "";
        SetVisible(_changeEmailForm, true);
        FormChanged?.Invoke(_newEmail.Input);
    }

    private void ShowConfirmEmailForm()
    {
        HideAllForms();
        _confirmEmailCode.Value = "";
        _confirmEmailMessage.text = $"We sent a 6-digit code to {_pendingNewEmail}.";
        SetVisible(_confirmEmailForm, true);
        FormChanged?.Invoke(_confirmEmailCode.Input);
    }

    private void ShowChangePasswordForm()
    {
        HideAllForms();
        _currentPassword.Value = "";
        _newPassword.Value = "";
        _confirmNewPassword.Value = "";
        SetVisible(_changePasswordForm, true);
        FormChanged?.Invoke(_currentPassword.Input);
    }

    private void HideAllForms()
    {
        _displayName.CancelEdit();

        SetVisible(_loginForm, false);
        SetVisible(_verifyForm, false);
        SetVisible(_forgotForm, false);
        SetVisible(_resetForm, false);
        SetVisible(_accountInfo, false);
        SetVisible(_changeEmailForm, false);
        SetVisible(_confirmEmailForm, false);
        SetVisible(_changePasswordForm, false);
        ClearErrors();
    }

    private async void OnLogin()
    {
        ClearErrors();
        var email = _loginEmail.Value;
        var password = _loginPassword.Value;
        var result = await _api.LoginAsync(email, password);

        if (result.Success)
        {
            if (result.Data != null && result.Data.requiresDeviceOtp)
            {
                // Unknown device — fall through to the verify form, but configure
                // it to complete a device OTP instead of the post-registration
                // email verification.
                _pendingVerificationEmail = email;
                _pendingDeviceVerificationPassword = password;
                _verifyMessage.text =
                    $"We sent a 6-digit code to {email} because this device isn't recognized.";
                ShowVerifyForm();
                return;
            }

            SyncServerDisplayName();
            ShowAccountInfo();
        }
        else
        {
            ShowError(_loginError, result.Error);
        }
    }

    private async void OnRegister()
    {
        ClearErrors();

        var result = await _api.RegisterAsync(
            _loginEmail.Value,
            _loginPassword.Value,
            GameSettings.DisplayName
        );

        if (result.Success)
        {
            _pendingVerificationEmail = _loginEmail.Value;
            _verifyMessage.text = $"We sent a 6-digit code to {_pendingVerificationEmail}.";
            ShowVerifyForm();
        }
        else
        {
            ShowError(_loginError, result.Error);
        }
    }

    private async void OnVerifyCode()
    {
        ClearErrors();
        SetVisible(_verifySuccess, false);

        if (string.IsNullOrEmpty(_pendingVerificationEmail))
            return;

        ApiResult<AuthResponse> result;
        if (!string.IsNullOrEmpty(_pendingDeviceVerificationPassword))
        {
            result = await _api.VerifyDeviceAsync(
                _pendingVerificationEmail,
                _pendingDeviceVerificationPassword,
                _verifyCode.Value
            );
        }
        else
        {
            result = await _api.VerifyCodeAsync(_pendingVerificationEmail, _verifyCode.Value);
        }

        if (result.Success)
        {
            _pendingDeviceVerificationPassword = null;
            SyncServerDisplayName();
            ShowAccountInfo();
        }
        else
        {
            ShowError(_verifyError, result.Error);
        }
    }

    private async void OnResendVerification()
    {
        ClearErrors();
        if (string.IsNullOrEmpty(_pendingVerificationEmail))
            return;

        var result = await _api.ResendVerificationAsync(_pendingVerificationEmail);

        if (result.Success)
        {
            _verifyMessage.text = "Verification email sent. Check your inbox.";
        }
        else
        {
            ShowError(_verifyError, result.Error);
        }
    }

    private async void OnForgotPassword()
    {
        ClearErrors();
        SetVisible(_forgotSuccess, false);

        var result = await _api.ForgotPasswordAsync(_forgotEmail.Value);

        if (result.Success)
        {
            _pendingResetEmail = _forgotEmail.Value;
            ShowResetForm();
        }
        else
        {
            ShowError(_forgotError, result.Error);
        }
    }

    private async void OnResetPassword()
    {
        ClearErrors();
        SetVisible(_resetSuccess, false);

        if (string.IsNullOrEmpty(_pendingResetEmail))
            return;

        if (_resetNewPassword.Value != _resetConfirmPassword.Value)
        {
            ShowError(_resetError, "Passwords do not match.");
            return;
        }

        var result = await _api.ResetPasswordAsync(
            _pendingResetEmail,
            _resetCode.Value,
            _resetNewPassword.Value
        );

        if (result.Success)
        {
            _resetSuccess.text = result.Data.message;
            SetVisible(_resetSuccess, true);
            _resetCode.Value = "";
            _resetNewPassword.Value = "";
            _resetConfirmPassword.Value = "";
        }
        else
        {
            ShowError(_resetError, result.Error);
        }
    }

    private async void OnDisplayNameCommit(string newName)
    {
        SetVisible(_displayNameError, false);

        // Always save locally first — works offline
        GameSettings.DisplayName = newName;
        UnityEngine.PlayerPrefs.SetString(GameSettings.DisplayNamePrefKey, newName);
        UnityEngine.PlayerPrefs.Save();

        // If logged in, also sync to server
        if (_api.IsLoggedIn)
        {
            var result = await _api.UpdateDisplayNameAsync(newName);
            if (result.Success)
                _displayName.Value = _api.DisplayName;
            else
                ShowError(_displayNameError, result.Error);
        }
    }

    private void OnCoopColorPreview(Color color)
    {
        ApplyColorToDisplayName(color);
    }

    private void ApplyColorToDisplayName(Color color)
    {
        // Tint the display name TextField's inner text element.
        var textInput = _displayName.Input?.Q(className: "unity-text-field__input");
        if (textInput != null)
            textInput.style.color = new StyleColor(color);
    }

    private async void OnCoopColorCommit(string hex)
    {
        PlayerPrefs.SetString("LocalCoopColor", hex);
        PlayerPrefs.Save();

        if (_api.IsLoggedIn)
        {
            var result = await _api.UpdateCoopColorAsync(hex);
            if (!result.Success && GlobalToast.Instance != null)
                GlobalToast.Instance.ShowError("Couldn't save color");
        }
    }

    private async void OnChangeEmail()
    {
        ClearErrors();

        var result = await _api.ChangeEmailAsync(_newEmail.Value, _confirmPassword.Value);

        if (result.Success)
        {
            _pendingNewEmail = _newEmail.Value;
            ShowConfirmEmailForm();
        }
        else
        {
            ShowError(_changeEmailError, result.Error);
        }
    }

    private async void OnConfirmEmailChange()
    {
        ClearErrors();

        if (string.IsNullOrEmpty(_pendingNewEmail))
            return;

        var result = await _api.ConfirmEmailChangeAsync(_pendingNewEmail, _confirmEmailCode.Value);

        if (result.Success)
        {
            ShowAccountInfo();
        }
        else
        {
            ShowError(_confirmEmailError, result.Error);
        }
    }

    private async void OnChangePassword()
    {
        ClearErrors();
        SetVisible(_changePasswordSuccess, false);

        if (_newPassword.Value != _confirmNewPassword.Value)
        {
            ShowError(_changePasswordError, "Passwords do not match.");
            return;
        }

        var result = await _api.ChangePasswordAsync(_currentPassword.Value, _newPassword.Value);

        if (result.Success)
        {
            _changePasswordSuccess.text = result.Data.message;
            SetVisible(_changePasswordSuccess, true);
            _currentPassword.Value = "";
            _newPassword.Value = "";
            _confirmNewPassword.Value = "";
        }
        else
        {
            ShowError(_changePasswordError, result.Error);
        }
    }

    /// <summary>Syncs the server display name to the local GameSettings store and UI.</summary>
    private void SyncServerDisplayName()
    {
        if (!string.IsNullOrEmpty(_api.DisplayName))
        {
            GameSettings.DisplayName = _api.DisplayName;
            _displayName.Value = _api.DisplayName;
        }
    }

    /// <summary>Cancels any in-progress inline edit (e.g. display name). Call when leaving the settings screen.</summary>
    public void CancelEditing() => _displayName.CancelEdit();

    /// <summary>Activate the display name EditableLabel from keyboard navigation.</summary>
    public void ActivateDisplayNameFromKeyboard() => _displayName.ActivateFromKeyboard();

    /// <summary>Items that should be linked horizontally (populated by GetFocusItems).</summary>
    public System.Collections.Generic.List<(int a, int b)> HorizontalPairs { get; } =
        new System.Collections.Generic.List<(int, int)>();

    /// <summary>
    /// Returns focus items for all currently visible interactive elements in the
    /// account section. Each item has a direct OnActivate callback — no event dispatch.
    /// Also populates <see cref="HorizontalPairs"/> for Left/Right links.
    /// </summary>
    public System.Collections.Generic.List<FocusNavigator.FocusItem> GetFocusItems()
    {
        var items = new System.Collections.Generic.List<FocusNavigator.FocusItem>();
        HorizontalPairs.Clear();

        // Display name — always visible. Root is highlighted; Enter enters edit mode.
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _displayName.Root,
                OnActivate = () =>
                {
                    ActivateDisplayNameFromKeyboard();
                    return true;
                },
            }
        );

        // Only add items from the currently visible form.
        if (IsVisible(_loginForm))
        {
            AddField(items, _loginEmail);
            AddField(items, _loginPassword);
            int loginIdx = items.Count;
            AddBtn(items, "login-submit-btn", OnLogin);
            int registerIdx = items.Count;
            AddBtn(items, "register-submit-btn", OnRegister);
            HorizontalPairs.Add((loginIdx, registerIdx));
        }
        else if (IsVisible(_verifyForm))
        {
            AddField(items, _verifyCode);
            AddBtn(items, "verify-submit-btn", OnVerifyCode);
            AddBtn(items, "resend-verify-btn", OnResendVerification);
            AddBtn(items, "verify-back-btn", ShowLoginForm);
        }
        else if (IsVisible(_forgotForm))
        {
            AddField(items, _forgotEmail);
            AddBtn(items, "forgot-submit-btn", OnForgotPassword);
            AddBtn(items, "forgot-back-btn", ShowLoginForm);
        }
        else if (IsVisible(_resetForm))
        {
            AddField(items, _resetCode);
            AddField(items, _resetNewPassword);
            AddField(items, _resetConfirmPassword);
            AddBtn(items, "reset-submit-btn", OnResetPassword);
            AddBtn(items, "reset-back-btn", ShowLoginForm);
        }
        else if (IsVisible(_accountInfo))
        {
            // Color picker sliders + hex field
            items.AddRange(_colorPicker.GetFocusItems());
            AddBtn(items, "change-email-btn", ShowChangeEmailForm);
            AddBtn(items, "change-password-btn", ShowChangePasswordForm);
            AddBtn(items, "logout-btn", () => _logoutModal.Show());
        }
        else if (IsVisible(_changeEmailForm))
        {
            AddField(items, _newEmail);
            AddField(items, _confirmPassword);
            AddBtn(items, "change-email-submit-btn", OnChangeEmail);
            AddBtn(items, "change-email-back-btn", () => ShowAccountInfo("change-email-btn"));
        }
        else if (IsVisible(_confirmEmailForm))
        {
            AddField(items, _confirmEmailCode);
            AddBtn(items, "confirm-email-submit-btn", OnConfirmEmailChange);
            AddBtn(items, "confirm-email-back-btn", () => ShowAccountInfo("change-email-btn"));
        }
        else if (IsVisible(_changePasswordForm))
        {
            AddField(items, _currentPassword);
            AddField(items, _newPassword);
            AddField(items, _confirmNewPassword);
            AddBtn(items, "change-password-submit-btn", OnChangePassword);
            AddBtn(items, "change-password-back-btn", () => ShowAccountInfo("change-password-btn"));
        }

        return items;
    }

    private void AddField(
        System.Collections.Generic.List<FocusNavigator.FocusItem> items,
        LabeledField field
    )
    {
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = field.Input,
                OnActivate =
                    field.OnSubmit != null
                        ? () =>
                        {
                            field.OnSubmit();
                            return true;
                        }
                        : (System.Func<bool>)null,
                OnFocused = () =>
                {
                    field.ActivateFromKeyboard();
                },
                OnBlurred = () =>
                {
                    field.Input.Blur();
                    if (KeybindManager.Instance != null)
                        KeybindManager.Instance.TextFieldFocused = false;
                },
            }
        );
    }

    private void AddBtn(
        System.Collections.Generic.List<FocusNavigator.FocusItem> items,
        string btnName,
        System.Action callback
    )
    {
        // Find button among all forms (it may be in any of them).
        var btn =
            _loginForm?.Q<UnityEngine.UIElements.Button>(btnName)
            ?? _verifyForm?.Q<UnityEngine.UIElements.Button>(btnName)
            ?? _forgotForm?.Q<UnityEngine.UIElements.Button>(btnName)
            ?? _resetForm?.Q<UnityEngine.UIElements.Button>(btnName)
            ?? _accountInfo?.Q<UnityEngine.UIElements.Button>(btnName)
            ?? _changeEmailForm?.Q<UnityEngine.UIElements.Button>(btnName)
            ?? _confirmEmailForm?.Q<UnityEngine.UIElements.Button>(btnName)
            ?? _changePasswordForm?.Q<UnityEngine.UIElements.Button>(btnName);
        if (btn == null)
            return;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = btn,
                OnActivate = () =>
                {
                    callback();
                    return true;
                },
            }
        );
    }

    private static bool IsVisible(UnityEngine.UIElements.VisualElement el)
    {
        return el != null && !el.ClassListContains("screen--hidden");
    }

    private async void OnLogoutConfirm()
    {
        _logoutModal.Hide();
        // LogoutAsync clears local session immediately, then best-effort
        // revokes the refresh token server-side. Don't await before showing
        // the login form — the UI flips instantly even if the network call
        // is slow or offline.
        var task = _api.LogoutAsync();
        ShowLoginForm();
        UpdateStatusLabel();
        await task;
    }

    private void ClearErrors()
    {
        SetVisible(_displayNameError, false);
        SetVisible(_loginError, false);
        SetVisible(_verifyError, false);
        SetVisible(_verifySuccess, false);
        SetVisible(_forgotError, false);
        SetVisible(_forgotSuccess, false);
        SetVisible(_resetError, false);
        SetVisible(_resetSuccess, false);
        SetVisible(_accountError, false);
        SetVisible(_changeEmailError, false);
        SetVisible(_confirmEmailError, false);
        SetVisible(_changePasswordError, false);
        SetVisible(_changePasswordSuccess, false);
    }

    private static string MaskEmail(string email)
    {
        if (string.IsNullOrEmpty(email))
            return "";

        var atIndex = email.IndexOf('@');
        if (atIndex <= 0)
            return email;

        var local = email.Substring(0, atIndex);
        var domain = email.Substring(atIndex);

        if (local.Length <= 2)
            return local[0] + new string('*', local.Length - 1) + domain;

        return local[0] + new string('*', local.Length - 2) + local[local.Length - 1] + domain;
    }

    private static void ShowError(Label label, string message)
    {
        label.text = message;
        label.RemoveFromClassList("screen--hidden");
    }

    private static void SetVisible(VisualElement el, bool visible)
    {
        if (visible)
            el.RemoveFromClassList("screen--hidden");
        else
            el.AddToClassList("screen--hidden");
    }
}
