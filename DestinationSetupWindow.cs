using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LivestreamStudio;

public sealed class DestinationSetupWindow : Window
{
    private readonly bool _isNewDestination;
    private readonly TextBox _nameInput;
    private readonly TextBox _serverInput;
    private readonly PasswordBox _streamKeyInput;
    private readonly TextBlock _validationMessage;

    public DestinationSetupWindow(Destination destination, bool isNewDestination)
    {
        _isNewDestination = isNewDestination;
        Title = isNewDestination ? "Add streaming destination" : $"Set up {destination.Name}";
        Width = 500;
        Height = isNewDestination ? 494 : 465;
        MinWidth = 500;
        MinHeight = Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 20, 29));
        Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 248));
        FontFamily = new FontFamily("Segoe UI");

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = isNewDestination ? "Add an RTMP destination" : $"Connect {destination.Name}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Enter the RTMP server address and the stream key provided by your streaming platform.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 153, 169)),
            Margin = new Thickness(0, 8, 0, 16)
        });

        if (isNewDestination)
        {
            content.Children.Add(CreateLabel("Destination name"));
        }

        _nameInput = new TextBox
        {
            Text = destination.Name,
            IsReadOnly = !isNewDestination,
            Height = 34,
            Padding = new Thickness(9, 5, 9, 5),
            Background = new SolidColorBrush(Color.FromRgb(23, 27, 38)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 47, 61))
        };
        if (isNewDestination)
        {
            content.Children.Add(_nameInput);
        }

        content.Children.Add(CreateLabel("RTMP server URL (without the stream key)"));
        _serverInput = new TextBox
        {
            Text = destination.ServerUrl,
            Height = 34,
            Padding = new Thickness(9, 5, 9, 5),
            Background = new SolidColorBrush(Color.FromRgb(23, 27, 38)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 47, 61)),
            ToolTip = "For example, rtmp://live.twitch.tv/app"
        };
        content.Children.Add(_serverInput);

        content.Children.Add(CreateLabel("Stream key"));
        _streamKeyInput = new PasswordBox
        {
            Height = 34,
            Padding = new Thickness(9, 5, 9, 5),
            Background = new SolidColorBrush(Color.FromRgb(23, 27, 38)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(42, 47, 61)),
            ToolTip = "Your stream key is encrypted for this Windows account before being saved."
        };
        content.Children.Add(_streamKeyInput);

        var keyHint = isNewDestination
            ? "Your key is encrypted for this Windows account before being saved."
            : destination.IsConfigured
                ? "A key is already stored securely. Leave this blank to keep it, or enter a new key to replace it."
                : "Your key is encrypted for this Windows account before being saved.";
        content.Children.Add(new TextBlock
        {
            Text = keyHint,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(148, 153, 169)),
            FontSize = 10,
            Margin = new Thickness(0, 6, 0, 0)
        });

        _validationMessage = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 130, 130)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        content.Children.Add(_validationMessage);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 80,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 9, 0)
        };
        var saveButton = new Button
        {
            Content = isNewDestination ? "Add destination" : "Save",
            IsDefault = true,
            MinWidth = 90,
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Color.FromRgb(115, 85, 238)),
            Foreground = Brushes.White
        };
        saveButton.Click += OnSaveClick;
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(saveButton);
        content.Children.Add(buttons);

        Content = content;
    }

    public string DestinationName => _nameInput.Text.Trim();

    public string ServerUrl => _serverInput.Text.Trim().TrimEnd('/');

    public string StreamKey => _streamKeyInput.Password.Trim();

    public bool HasNewStreamKey => !string.IsNullOrWhiteSpace(StreamKey);

    private static TextBlock CreateLabel(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 11, 0, 6)
    };

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_isNewDestination && string.IsNullOrWhiteSpace(DestinationName))
        {
            _validationMessage.Text = "Enter a name for this destination.";
            _nameInput.Focus();
            return;
        }

        if (!BroadcastService.IsValidRtmpServerUrl(ServerUrl))
        {
            _validationMessage.Text = "Enter a valid rtmp:// or rtmps:// server URL without a stream key.";
            _serverInput.Focus();
            return;
        }

        if (_isNewDestination && !HasNewStreamKey)
        {
            _validationMessage.Text = "Enter the stream key supplied by the destination platform.";
            _streamKeyInput.Focus();
            return;
        }

        DialogResult = true;
    }
}
