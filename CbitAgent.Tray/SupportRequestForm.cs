namespace CbitAgent.Tray;

public class SupportRequestForm : Form
{
    private readonly TrayApiClient _apiClient;
    // L12: No screenshot captured at construction time — deferred until user opts in via checkbox.
    // _capturedScreenshot holds the bitmap once captured; disposed on uncheck.
    private Bitmap? _capturedScreenshot;

    private TextBox _emailBox = null!;
    private TextBox _descriptionBox = null!;
    private CheckBox _includeScreenshotCheck = null!;
    private PictureBox _thumbnailBox = null!;
    private Button _captureButton = null!;
    private Button _submitButton = null!;
    private Button _cancelButton = null!;
    private Label _statusLabel = null!;

    public SupportRequestForm(TrayApiClient apiClient)
    {
        _apiClient = apiClient;
        InitializeComponents();
        // Thumbnail starts empty — checkbox is unchecked, no screenshot taken yet
    }

    private void InitializeComponents()
    {
        Text = "CBIT — Submit Support Request";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(500, 500);

        // Email label
        var emailLabel = new Label
        {
            Text = "Email (optional)",
            Location = new Point(12, 12),
            AutoSize = true
        };
        Controls.Add(emailLabel);

        // Email textbox
        _emailBox = new TextBox
        {
            Location = new Point(12, 32),
            Size = new Size(476, 23),
            PlaceholderText = "For faster response, add your email"
        };
        Controls.Add(_emailBox);

        // Description label
        var descLabel = new Label
        {
            Text = "Describe your issue:",
            Location = new Point(12, 64),
            AutoSize = true
        };
        Controls.Add(descLabel);

        // Description textbox
        _descriptionBox = new TextBox
        {
            Location = new Point(12, 84),
            Size = new Size(476, 100),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            AcceptsReturn = true
        };
        Controls.Add(_descriptionBox);

        // Screenshot checkbox — L12: starts unchecked; screenshot captured on first check
        _includeScreenshotCheck = new CheckBox
        {
            Text = "Include screenshot",
            Location = new Point(12, 192),
            Checked = false,
            AutoSize = true
        };
        _includeScreenshotCheck.CheckedChanged += OnScreenshotCheckChanged;
        Controls.Add(_includeScreenshotCheck);

        // Capture new screenshot button
        _captureButton = new Button
        {
            Text = "Capture New Screenshot",
            Location = new Point(340, 189),
            Size = new Size(148, 25)
        };
        _captureButton.Click += OnCaptureNewScreenshot;
        Controls.Add(_captureButton);

        // Thumbnail preview
        _thumbnailBox = new PictureBox
        {
            Location = new Point(12, 222),
            Size = new Size(476, 220),
            BorderStyle = BorderStyle.FixedSingle,
            SizeMode = PictureBoxSizeMode.CenterImage,
            BackColor = Color.FromArgb(240, 240, 240)
        };
        Controls.Add(_thumbnailBox);

        // Status label
        _statusLabel = new Label
        {
            Text = "",
            Location = new Point(12, 452),
            Size = new Size(280, 20),
            ForeColor = Color.Gray
        };
        Controls.Add(_statusLabel);

        // Submit button
        _submitButton = new Button
        {
            Text = "Submit",
            Location = new Point(320, 448),
            Size = new Size(80, 28)
        };
        _submitButton.Click += OnSubmit;
        Controls.Add(_submitButton);

        // Cancel button
        _cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(408, 448),
            Size = new Size(80, 28)
        };
        _cancelButton.Click += (_, _) => Close();
        Controls.Add(_cancelButton);

        AcceptButton = _submitButton;
        CancelButton = _cancelButton;
    }

    /// <summary>
    /// L12: Called when the "Include screenshot" checkbox changes state.
    /// On check: capture a fresh screenshot immediately (form is still visible — user sees it in preview).
    /// On uncheck: dispose the captured screenshot and clear the thumbnail.
    /// </summary>
    private void OnScreenshotCheckChanged(object? sender, EventArgs e)
    {
        if (_includeScreenshotCheck.Checked)
        {
            // Capture screenshot now — form is visible but that's expected (user just opted in)
            var capture = ScreenshotCapture.CaptureFullScreen();
            if (capture != null)
            {
                _capturedScreenshot?.Dispose();
                _capturedScreenshot = capture;
                UpdateThumbnail();
            }
            else
            {
                // Capture failed — revert checkbox
                _includeScreenshotCheck.Checked = false;
                MessageBox.Show("Could not capture screenshot.", "Screenshot",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else
        {
            // User opted out — dispose and clear
            _capturedScreenshot?.Dispose();
            _capturedScreenshot = null;
            _thumbnailBox.Image?.Dispose();
            _thumbnailBox.Image = null;
            _thumbnailBox.Tag = null;
        }
    }

    private void UpdateThumbnail()
    {
        _thumbnailBox.Image?.Dispose();
        _thumbnailBox.Image = null;

        var bmp = (_thumbnailBox.Tag as Bitmap) ?? _capturedScreenshot;
        if (_includeScreenshotCheck.Checked && bmp != null)
        {
            _thumbnailBox.Image = ScreenshotCapture.CreateThumbnail(
                bmp, _thumbnailBox.Width - 4, _thumbnailBox.Height - 4);
        }
    }

    private void OnCaptureNewScreenshot(object? sender, EventArgs e)
    {
        // Hide form, wait a moment, capture, show form
        Visible = false;
        Application.DoEvents();
        Thread.Sleep(300); // Let the form fully disappear

        var newCapture = ScreenshotCapture.CaptureFullScreen();
        Visible = true;

        if (newCapture != null)
        {
            _capturedScreenshot?.Dispose();
            _capturedScreenshot = newCapture;
            // Also clear any re-capture stored in Tag
            (_thumbnailBox.Tag as Bitmap)?.Dispose();
            _thumbnailBox.Tag = null;

            _thumbnailBox.Image?.Dispose();
            _thumbnailBox.Image = ScreenshotCapture.CreateThumbnail(
                newCapture, _thumbnailBox.Width - 4, _thumbnailBox.Height - 4);
        }
    }

    private async void OnSubmit(object? sender, EventArgs e)
    {
        var description = _descriptionBox.Text.Trim();
        if (string.IsNullOrEmpty(description))
        {
            MessageBox.Show("Please describe your issue.", "Required",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _descriptionBox.Focus();
            return;
        }

        _submitButton.Enabled = false;
        _cancelButton.Enabled = false;
        _statusLabel.Text = "Submitting...";
        _statusLabel.ForeColor = Color.Gray;

        try
        {
            byte[]? screenshotBytes = null;
            if (_includeScreenshotCheck.Checked)
            {
                // Use re-captured screenshot if available, otherwise the checkbox-captured one
                var bmp = (_thumbnailBox.Tag as Bitmap) ?? _capturedScreenshot;
                if (bmp != null)
                    screenshotBytes = ScreenshotCapture.ToBytes(bmp);
            }

            var email = _emailBox.Text.Trim();

            var (success, ticketNumber, errorMessage) = await _apiClient.SubmitSupportRequestAsync(
                description, Environment.UserName, email, screenshotBytes);

            if (success)
            {
                MessageBox.Show(
                    $"Support request submitted successfully!\n\nTicket: {ticketNumber}",
                    "CBIT Support", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            else
            {
                _statusLabel.Text = "Submission failed";
                _statusLabel.ForeColor = Color.Red;
                MessageBox.Show(
                    $"Failed to submit support request.\n\n{errorMessage}\n\n" +
                    "Please contact CBIT directly:\n" +
                    "Phone: (509) 578-5424\n" +
                    "Email: support@gocbit.com",
                    "CBIT Support — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Error";
            _statusLabel.ForeColor = Color.Red;
            MessageBox.Show(
                $"An error occurred:\n\n{ex.Message}\n\n" +
                "Please contact CBIT directly:\n" +
                "Phone: (509) 578-5424\n" +
                "Email: support@gocbit.com",
                "CBIT Support — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _submitButton.Enabled = true;
            _cancelButton.Enabled = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnailBox.Image?.Dispose();
            (_thumbnailBox.Tag as Bitmap)?.Dispose();
            _capturedScreenshot?.Dispose();
        }
        base.Dispose(disposing);
    }
}
