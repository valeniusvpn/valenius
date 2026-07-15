/// Profile names are `[A-Za-z0-9_-]{1,50}` — the same invariant enforced on
/// Windows (`ProfileNameHelper.IsValid`) and Linux (`is_valid_profile_name`).
final _profileNameRe = RegExp(r'^[A-Za-z0-9_-]{1,50}$');

bool isValidProfileName(String name) => _profileNameRe.hasMatch(name);

/// Strip a trailing `.conf` (case-insensitive) from a delivered file name.
String profileNameFromFileName(String fileName) {
  final f = fileName.trim();
  if (f.toLowerCase().endsWith('.conf')) {
    return f.substring(0, f.length - 5);
  }
  return f;
}
