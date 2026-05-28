namespace FocusLock.UI.Controls;

public static class TimeInputHelper
{
  public static string Normalize(string? raw, TimePartKind kind)
  {
    if (string.IsNullOrWhiteSpace(raw))
      return kind == TimePartKind.Hour12 ? "12" : "00";

    if (!int.TryParse(raw.Trim(), out int n))
      return kind == TimePartKind.Hour12 ? "12" : "00";

    if (kind == TimePartKind.Hour12)
    {
      n = Math.Clamp(n, 1, 12);
      return n.ToString();
    }

    n = Math.Clamp(n, 0, 59);
    return n.ToString("00");
  }

  public static string Increment(string? raw, TimePartKind kind)
  {
    var norm = Normalize(raw, kind);
    int n = int.Parse(norm);
    if (kind == TimePartKind.Hour12)
      n = n >= 12 ? 1 : n + 1;
    else
      n = n >= 59 ? 0 : n + 1;
    return kind == TimePartKind.Minute ? n.ToString("00") : n.ToString();
  }

  public static string Decrement(string? raw, TimePartKind kind)
  {
    var norm = Normalize(raw, kind);
    int n = int.Parse(norm);
    if (kind == TimePartKind.Hour12)
      n = n <= 1 ? 12 : n - 1;
    else
      n = n <= 0 ? 59 : n - 1;
    return kind == TimePartKind.Minute ? n.ToString("00") : n.ToString();
  }
}
