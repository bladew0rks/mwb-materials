using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace mwb_materials
{
    static class MessageDialog
    {
        public static Task Show(Window owner, string title, string message)
        {
            Button okButton = new Button()
            {
                Content = "OK",
                MinWidth = 80,
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };

            Window dialog = new Window()
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = owner.Background,
                Content = new StackPanel()
                {
                    Margin = new Avalonia.Thickness(20),
                    Spacing = 16,
                    Children =
                    {
                        new TextBlock() { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                        new SelectableTextBlock() { Text = message, TextWrapping = TextWrapping.Wrap },
                        okButton
                    }
                }
            };

            okButton.Click += (sender, args) => dialog.Close();
            return dialog.ShowDialog(owner);
        }
    }
}
