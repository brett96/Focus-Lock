using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FocusLock.UI.Controls;

public partial class TimePartSpinBox : System.Windows.Controls.UserControl
{
  private static readonly Regex DigitsOnly = new(@"^\d$");
  private bool _syncing;

  public static readonly DependencyProperty ValueProperty =
      DependencyProperty.Register(
          nameof(Value),
          typeof(string),
          typeof(TimePartSpinBox),
          new FrameworkPropertyMetadata("12", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

  public static readonly DependencyProperty PartProperty =
      DependencyProperty.Register(
          nameof(Part),
          typeof(TimePartKind),
          typeof(TimePartSpinBox),
          new PropertyMetadata(TimePartKind.Hour12, OnPartChanged));

  public string Value
  {
    get => (string)GetValue(ValueProperty);
    set => SetValue(ValueProperty, value);
  }

  public TimePartKind Part
  {
    get => (TimePartKind)GetValue(PartProperty);
    set => SetValue(PartProperty, value);
  }

  public TimePartSpinBox()
  {
    InitializeComponent();
    Loaded += (_, _) => SyncTextFromValue();
  }

  private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
  {
    if (d is TimePartSpinBox box && !box._syncing)
      box.SyncTextFromValue();
  }

  private static void OnPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
  {
    if (d is TimePartSpinBox box)
      box.CoerceAndPublish();
  }

  private void SyncTextFromValue()
  {
    _syncing = true;
    PartTextBox.Text = TimeInputHelper.Normalize(Value, Part);
    _syncing = false;
  }

  private void CoerceAndPublish()
  {
    _syncing = true;
    var normalized = TimeInputHelper.Normalize(Value, Part);
    Value = normalized;
    PartTextBox.Text = normalized;
    _syncing = false;
  }

  private void PartTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
      => e.Handled = !DigitsOnly.IsMatch(e.Text);

  private void PartTextBox_LostFocus(object sender, RoutedEventArgs e)
  {
    _syncing = true;
    Value = PartTextBox.Text;
    _syncing = false;
    CoerceAndPublish();
  }

  private void Increment_Click(object sender, RoutedEventArgs e)
  {
    Value = TimeInputHelper.Increment(Value, Part);
    CoerceAndPublish();
    PartTextBox.Focus();
  }

  private void Decrement_Click(object sender, RoutedEventArgs e)
  {
    Value = TimeInputHelper.Decrement(Value, Part);
    CoerceAndPublish();
    PartTextBox.Focus();
  }
}
