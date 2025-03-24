using System;
using System.Linq;

namespace GenUtilities;

public static class StringExtension {
	public static string SnakeToCamelCase(this string str) {
		return str.Split(["_"], StringSplitOptions.RemoveEmptyEntries)
			.Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1, s.Length - 1))
			.Aggregate(string.Empty, (s1, s2) => s1 + s2);
	}
}