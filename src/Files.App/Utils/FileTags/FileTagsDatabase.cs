// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Win32;
using System.Runtime.CompilerServices;
using System.Security;
using Windows.ApplicationModel;
using static Files.App.Helpers.RegistryHelpers;
using static Files.App.Utils.FileTags.TaggedFileRegistry;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Files.App.Utils.FileTags
{
	public sealed class FileTagsDatabase
	{
		private static string? _FileTagsKey;
		private string? FileTagsKey => _FileTagsKey ??= SafetyExtensions.IgnoreExceptions(() => @$"Software\Files Community\{Package.Current.Id.Name}\v1\FileTags");

		private string GetRegistrySubPathForFilePath(string absoluteFilePath)
		{
			if (string.IsNullOrEmpty(absoluteFilePath))
				return string.Empty;

			var drive = Path.GetPathRoot(absoluteFilePath);
			if (string.IsNullOrEmpty(drive))
				return absoluteFilePath.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');

			var relativePath = absoluteFilePath.Substring(drive.Length);
			drive = drive.Replace(":", "").Replace(Path.DirectorySeparatorChar.ToString(), ""); // C: -> C, C:\ -> C

			return $"DRIVE_{drive}\\{relativePath}";
		}

		public void SetTags(string filePath, ulong? frn, string[] tags)
		{
			if (FileTagsKey is null)
				return;

			var relativeKeyPath = GetRegistrySubPathForFilePath(filePath);
			using var filePathKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, relativeKeyPath));

			if (tags is [])
			{
				SaveValues(filePathKey, null); // This will clear the values if tags are empty
				if (frn is not null)
				{
					// Also clear the FRN-based entry if tags are empty
					using var frnKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, "FRN", frn.Value.ToString()));
					SaveValues(frnKey, null);
				}

				return;
			}

			var newTag = new TaggedFile()
			{
				FilePath = filePath, // Store original filePath
				Frn = frn,
				Tags = tags
			};
			SaveValues(filePathKey, newTag); // This should save FilePath, Frn, and Tags

			if (frn is not null)
			{
				// Ensure the FRN-based storage also has the original FilePath
				using var frnKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, "FRN", frn.Value.ToString()));
				SaveValues(frnKey, newTag);
			}
		}

		private TaggedFile? FindTag(string? filePath, ulong? frn)
		{
			if (FileTagsKey is null)
				return null;

			if (filePath is not null)
			{
				var relativeKeyPath = GetRegistrySubPathForFilePath(filePath);
				// Use OpenSubKey for reading to avoid creating a key if it doesn't exist.
				using var filePathKey = Registry.CurrentUser.OpenSubKey(CombineKeys(FileTagsKey, relativeKeyPath));
				if (filePathKey is not null && filePathKey.ValueCount > 0)
				{
					var tag = new TaggedFile();
					BindValues(filePathKey, tag); // Reads values from the key into tag object
					// Ensure the FilePath property of the tag object is the original absolute path.
					// The FilePath value stored in the registry *is* the original absolute path.
					tag.FilePath = (string?)filePathKey.GetValue(nameof(TaggedFile.FilePath)) ?? filePath;

					if (frn is not null && tag.Frn != frn)
					{
						// If FRN is provided and it's different from the stored FRN, update the FRN in the registry.
						// This might happen if the file was moved or its FRN changed.
						tag.Frn = frn;
						var value = frn.Value;
						// Re-open with write access (CreateSubKey ensures the key exists and is writable).
						using var writableFilePathKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, relativeKeyPath));
						writableFilePathKey.SetValue(nameof(TaggedFile.Frn), Unsafe.As<ulong, long>(ref value), RegistryValueKind.QWord);
					}
					return tag;
				}
			}

			if (frn is not null)
			{
				// Use OpenSubKey for reading.
				using var frnKey = Registry.CurrentUser.OpenSubKey(CombineKeys(FileTagsKey, "FRN", frn.Value.ToString()));
				if (frnKey is not null && frnKey.ValueCount > 0)
				{
					var tag = new TaggedFile();
					BindValues(frnKey, tag); // Reads values including the original FilePath
					// Ensure the FilePath property is correctly populated from the registry.
					tag.FilePath = (string?)frnKey.GetValue(nameof(TaggedFile.FilePath)) ?? tag.FilePath;

					if (filePath is not null && tag.FilePath != filePath)
					{
						// If filePath is provided and it's different, update it in this FRN-based key.
						tag.FilePath = filePath;
						// Re-open with write access.
						using var writableFrnKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, "FRN", frn.Value.ToString()));
						writableFrnKey.SetValue(nameof(TaggedFile.FilePath), filePath, RegistryValueKind.String);
					}
					return tag;
				}
			}

			return null;
		}

		public void UpdateTag(string oldFilePath, ulong? frn, string? newFilePath)
		{
			if (FileTagsKey is null)
				return;

			var oldRelativeKeyPath = GetRegistrySubPathForFilePath(oldFilePath);
			var tag = FindTag(oldFilePath, null); // FindTag now uses transformed paths implicitly

			// Attempt to open the old key for modification/deletion.
			// CreateSubKey is used here to ensure the key path is treated consistently (created if not exist, then values cleared).
			// However, a more robust approach might be OpenSubKey with write access, then DeleteSubKeyTree if needed.
			// For now, sticking to clearing values via SaveValues(key, null).
			using var oldPathRegistryKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, oldRelativeKeyPath));
			SaveValues(oldPathRegistryKey, null); // Clear values at the old path-based key

			if (tag is not null)
			{
				tag.Frn = frn ?? tag.Frn;
				// newFilePath is the original, untransformed path for the new location
				tag.FilePath = newFilePath ?? tag.FilePath;

				// Update FRN-based entry (if FRN exists)
				if (tag.Frn is not null)
				{
					using var frnRegistryKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, "FRN", tag.Frn.Value.ToString()));
					SaveValues(frnRegistryKey, tag); // Save updated tag (with new FilePath if changed)
				}

				// Create new path-based entry if newFilePath is provided
				if (!string.IsNullOrEmpty(newFilePath))
				{
					var newRelativeKeyPath = GetRegistrySubPathForFilePath(newFilePath);
					using var newPathRegistryKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, newRelativeKeyPath));
					SaveValues(newPathRegistryKey, tag); // Save tag with original newFilePath
				}
			}
		}

		public void UpdateTag(ulong oldFrn, ulong? frn, string? newFilePath)
		{
			if (FileTagsKey is null)
				return;

			var tag = FindTag(null, oldFrn); // Find by old FRN

			// Delete the old FRN-based entry by clearing its values.
			// CreateSubKey ensures the key exists before attempting to clear its values.
			using var oldFrnRegistryKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, "FRN", oldFrn.ToString()));
			SaveValues(oldFrnRegistryKey, null);

			if (tag is not null)
			{
				tag.Frn = frn ?? tag.Frn; // Update FRN if new one is provided
				// newFilePath is the original, untransformed path for the new location
				tag.FilePath = newFilePath ?? tag.FilePath; // Update FilePath if new one is provided


				// Update/create FRN-based entry with the new FRN (if provided)
				if (tag.Frn is not null)
				{
					using var newFrnRegistryKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, "FRN", tag.Frn.Value.ToString()));
					SaveValues(newFrnRegistryKey, tag); // Save updated tag
				}

				// Update/create path-based entry with the new FilePath (if provided and valid)
				if (!string.IsNullOrEmpty(tag.FilePath))
				{
					var relativeNewPath = GetRegistrySubPathForFilePath(tag.FilePath);
					using var newFilePathRegistryKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, relativeNewPath));
					SaveValues(newFilePathRegistryKey, tag); // Save tag with original FilePath
				}
			}
		}

		public string[] GetTags(string? filePath, ulong? frn)
		{
			return FindTag(filePath, frn)?.Tags ?? [];
		}

		public IEnumerable<TaggedFile> GetAll()
		{
			var list = new List<TaggedFile>();

			if (FileTagsKey is not null)
			{
				try
				{
					IterateKeys(list, FileTagsKey, 0);
				}
				catch (SecurityException)
				{
					// Handle edge case where IterateKeys results in SecurityException
				}
			}

			return list;
		}

		public IEnumerable<TaggedFile> GetAllUnderPath(string folderPath)
		{
			var list = new List<TaggedFile>();
			if (FileTagsKey is null)
				return list;

			string startRegistryPath;
			if (string.IsNullOrEmpty(folderPath))
			{
				startRegistryPath = FileTagsKey;
			}
			else
			{
				// Transform the absolute folderPath to the relative registry path structure
				var relativeStartPath = GetRegistrySubPathForFilePath(folderPath);
				startRegistryPath = CombineKeys(FileTagsKey, relativeStartPath);
			}

			try
			{
				IterateKeys(list, startRegistryPath, 0);
			}
			catch (SecurityException)
			{
				// Handle edge case where IterateKeys results in SecurityException
			}

			return list;
		}

		public void Import(string json)
		{
			if (FileTagsKey is null)
				return;

			var tags = JsonSerializer.Deserialize<TaggedFile[]>(json);

			Registry.CurrentUser.DeleteSubKeyTree(FileTagsKey, false); // Clear existing tags
			if (tags is null)
			{
				return;
			}
			foreach (var tag in tags)
			{
				// Ensure FilePath is not null or empty before processing
				if (string.IsNullOrEmpty(tag.FilePath))
					continue;

				var relativeKeyPath = GetRegistrySubPathForFilePath(tag.FilePath);
				using var filePathKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, relativeKeyPath));
				// Save the tag, which includes the original FilePath, Frn, and Tags
				SaveValues(filePathKey, tag);

				if (tag.Frn is not null)
				{
					using var frnKey = Registry.CurrentUser.CreateSubKey(CombineKeys(FileTagsKey, "FRN", tag.Frn.Value.ToString()));
					// Also save the tag here, ensuring it includes the original FilePath
					SaveValues(frnKey, tag);
				}
			}
		}

		public string Export()
		{
			var list = new List<TaggedFile>();

			if (FileTagsKey is not null)
				IterateKeys(list, FileTagsKey, 0);

			return JsonSerializer.Serialize(list);
		}

		private void IterateKeys(List<TaggedFile> list, string path, int depth)
		{
			using var key = Registry.CurrentUser.OpenSubKey(path);
			if (key is null)
				return;

			// Check if the current key itself represents a tagged file by having values.
			if (key.ValueCount > 0)
			{
				var tag = new TaggedFile();
				BindValues(key, tag); // This reads Frn, Tags, and importantly, the original FilePath

				// Ensure FilePath is correctly loaded, it should be the absolute path.
				// BindValues should already populate tag.FilePath with the value from the "FilePath" registry value.
				// If tag.FilePath is null or empty after BindValues, something is wrong or it's an old/invalid entry.
				// We should only add it if FilePath is present, as it's crucial.
				if (!string.IsNullOrEmpty(tag.FilePath))
				{
					// Check if this item is already added (e.g. through FRN link and direct path processing)
					// This check might be too simplistic if FRNs can be null or paths can change independently.
					// A more robust duplicate check might involve comparing FRNs if available.
					if (!list.Any(existingTag => existingTag.FilePath == tag.FilePath && existingTag.Frn == tag.Frn))
					{
						list.Add(tag);
					}
				}
			}

			// Recursively iterate through subkeys.
			// These subkeys form parts of the transformed path (e.g., "DRIVE_C", "Users", "Doc.txt").
			foreach (var subKeyName in key.GetSubKeyNames())
			{
				// Skip the global "FRN" directory at the root of FileTagsKey during path-based iteration.
				// FRN entries are typically not directly under a path-transformed key unless it's a root FRN key.
				if (depth == 0 && subKeyName.Equals("FRN", StringComparison.OrdinalIgnoreCase) && path.Equals(FileTagsKey, StringComparison.OrdinalIgnoreCase))
					continue;

				IterateKeys(list, CombineKeys(path, subKeyName), depth + 1);
			}
		}
	}
}