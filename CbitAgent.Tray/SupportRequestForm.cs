namespace CbitAgent.Tray;

public class SupportRequestForm : Form
{
    private readonly TrayApiClient _apiClient;
    private readonly Bitmap? _screenshot;

    private TextBox _emailBox = null!;
    private TextBox _descriptionBox = null!;
    private CheckBox _includeScreenshotCheck = null!;
    private PictureBox _thumbnailBox = null!;
    private Button _captureButton = null!;
    private Button _submitButton = null!;
    private Button _cancelButton = null!;
    private Label _statusLabel = null!;

    public SupportRequestForm(TrayApiClient apiClient, Bitmap? screenshot)
    {
        _apiClient = apiClient;
        _screenshot = screenshot;
        InitializeComponents();
        UpdateThumbnail();
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

        // Screenshot checkbox
        _includeScreenshotCheck = new CheckBox
        {
            Text = "Include screenshot",
            Location = new Point(12, 192),
            Checked = true,
            AutoSize = true
        };
        _includeScreenshotCheck.CheckedChanged += (_, _) => UpdateThumbnail();
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

    private void UpdateThumbnail()
    {
        _thumbnailBox.Image?.Dispose();
        _thumbnailBox.Image = null;

        if (_includeScreenshotCheck.Checked && _screenshot != null)
        {
            _thumbnailBox.Image = ScreenshotCapture.CreateThumbnail(
                _screenshot, _thumbnailBox.Width - 4, _thumbnailBox.Height - 4);
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
            // Replace the screenshot - we can't reassign _screenshot (readonly),
            // but we can update the thumbnail from the new capture
            _thumbnailBox.Image?.Dispose();
            _thumbnailBox.Image = ScreenshotCapture.CreateThumbnail(
                newCapture, _thumbnailBox.Width - 4, _thumbnailBox.Height - 4);
            _thumbnailBox.Tag = newCapture; // Store for submit
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
                // Use re-captured screenshot if available, otherwise original
                var bmp = (_thumbnailBox.Tag as Bitmap) ?? _screenshot;
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
        }
        base.Dispose(disposing);
    }
}
