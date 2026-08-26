using System.Windows;
using TNovCloud.Shared;

namespace TNovCloud.Client;

public partial class ApproveWindow : Window
{
    public string ProcessTitle => string.IsNullOrWhiteSpace(TitleBox.Text) ? "Согласование" : TitleBox.Text.Trim();
    public IReadOnlyList<Guid> AssigneeIds => UserList.SelectedItems.Cast<UserDto>().Select(x => x.Id).ToList();
    public DateTimeOffset? DueAt => DateTimeOffset.TryParse(DueBox.Text, out var value) ? value : null;

    public ApproveWindow(IReadOnlyList<UserDto> users)
    {
        InitializeComponent();
        UserList.ItemsSource = users;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (AssigneeIds.Count == 0)
        {
            MessageBox.Show("Выберите хотя бы одного согласующего.", "TNovCloud");
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
