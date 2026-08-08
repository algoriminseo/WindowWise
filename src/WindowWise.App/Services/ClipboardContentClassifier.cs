using WindowWise.Models;

namespace WindowWise.Services;

public static class ClipboardContentClassifier
{

    /// <synnary>
    /// distinguish the content type for the clipboard content, either text or link
    /// </synnary>
    public static ClipboardType Classify(string content)
    {
        if (!Uri.TryCreate(content, UriKind.Absolute, out var uri) ||
            uri is null)
        {
            return ClipboardType.Text;
        }

        bool isHttp = uri.Scheme == Uri.UriSchemeHttp;
        bool isHttps = uri.Scheme == Uri.UriSchemeHttps;

        if (isHttp || isHttps)
        {
            return ClipboardType.Link;
        }

        return ClipboardType.Text;
    }


    private static bool LooksLikeCode(string content)
    {
        string trimmedContent = content.Trim();

        if (string.IsNullOrWhiteSpace(trimmedContent))
        {
            return false;
        }

        string[] codeSignals =
        [
            // C / C++ / C# / Java
            "#include",
        "using ",
        "namespace ",
        "class ",
        "interface ",
        "enum ",
        "struct ",
        "public ",
        "private ",
        "protected ",
        "static ",
        "void ",
        "return ",
        "new ",
        "try ",
        "catch ",
        "finally",
        "throw ",
        "extends ",
        "implements ",
        "import ",

        // JavaScript / TypeScript
        "function ",
        "const ",
        "let ",
        "var ",
        "=>",
        "export ",
        "default ",
        "async ",
        "await ",
        "console.log",

        // Python
        "def ",
        "class ",
        "import ",
        "from ",
        "self.",
        "elif ",
        "except ",
        "lambda ",
        "print(",

        // SQL
        "select ",
        "insert into ",
        "update ",
        "delete from ",
        "create table ",
        "alter table ",
        "drop table ",
        "where ",
        "join ",
        "group by ",
        "order by ",

        // HTML / XML
        "<html",
        "<div",
        "<span",
        "<body",
        "<script",
        "<style",
        "</",
        "<?xml",

        // CSS
        "display:",
        "position:",
        "margin:",
        "padding:",
        "color:",
        "background:",
        "font-size:",

        // Shell / PowerShell
        "#!/bin/",
        "sudo ",
        "chmod ",
        "npm ",
        "git ",
        "dotnet ",
        "Get-",
        "Set-",
        "$env:"
        ];

        int signalCount = codeSignals.Count(signal =>
            trimmedContent.Contains(signal, StringComparison.OrdinalIgnoreCase));

        if (signalCount >= 2)
        {
            return true;
        }

        if (LooksLikeCodeComment(trimmedContent))
        {
            return true;
        }

        bool hasCodePunctuation =
            trimmedContent.Contains('{') ||
            trimmedContent.Contains('}') ||
            trimmedContent.Contains(';') ||
            trimmedContent.Contains("=>", StringComparison.Ordinal) ||
            trimmedContent.Contains("==", StringComparison.Ordinal) ||
            trimmedContent.Contains("!=", StringComparison.Ordinal) ||
            trimmedContent.Contains("<=", StringComparison.Ordinal) ||
            trimmedContent.Contains(">=", StringComparison.Ordinal);

        bool hasLineStructure =
            trimmedContent.Contains('\n') ||
            trimmedContent.Contains('\r');

        if (hasCodePunctuation && hasLineStructure)
        {
            return true;
        }

        bool looksPythonBlock =
            trimmedContent.Contains(":", StringComparison.Ordinal) &&
            (
                trimmedContent.StartsWith("def ", StringComparison.OrdinalIgnoreCase) ||
                trimmedContent.StartsWith("class ", StringComparison.OrdinalIgnoreCase) ||
                trimmedContent.StartsWith("if ", StringComparison.OrdinalIgnoreCase) ||
                trimmedContent.StartsWith("for ", StringComparison.OrdinalIgnoreCase) ||
                trimmedContent.StartsWith("while ", StringComparison.OrdinalIgnoreCase) ||
                trimmedContent.StartsWith("try:", StringComparison.OrdinalIgnoreCase) ||
                trimmedContent.StartsWith("except", StringComparison.OrdinalIgnoreCase)
            );

        if (looksPythonBlock)
        {
            return true;
        }

        bool looksLikeHtmlTag =
            trimmedContent.StartsWith("<", StringComparison.Ordinal) &&
            trimmedContent.Contains(">", StringComparison.Ordinal);

        if (looksLikeHtmlTag)
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeCodeComment(string content)
    {
        string trimmedContent = content.Trim();

        if (trimmedContent.StartsWith("///", StringComparison.Ordinal) ||
            trimmedContent.StartsWith("//", StringComparison.Ordinal) ||
            trimmedContent.StartsWith("/*", StringComparison.Ordinal) ||
            trimmedContent.StartsWith("*", StringComparison.Ordinal) ||
            trimmedContent.StartsWith("<!--", StringComparison.Ordinal) ||
            trimmedContent.StartsWith("#", StringComparison.Ordinal) ||
            trimmedContent.StartsWith("--", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmedContent.EndsWith("*/", StringComparison.Ordinal) ||
            trimmedContent.EndsWith("-->", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static ClipboardCategoryResult ClassifyCategory(string content)
    {
        if(string.IsNullOrWhiteSpace(content))
        {
            return new ClipboardCategoryResult
            {
                Category = "Unknown",
                SubCategory = null,
                SuggestedCategory = null,
                IsAiCategorized = false
            };
        }

        if (Uri.TryCreate(content, UriKind.Absolute, out var uri) &&
           uri is not null &&
           (uri.Scheme == Uri.UriSchemeHttp ||
            uri.Scheme == Uri.UriSchemeHttps))
        {
            return new ClipboardCategoryResult
            {
                Category = "Link",
                SubCategory = "Website",
                SuggestedCategory = "Website link",
                IsAiCategorized = false
            };

        }

        if(content.Contains("@", StringComparison.OrdinalIgnoreCase) &&
            content.Contains(".", StringComparison.OrdinalIgnoreCase))
        {
            return new ClipboardCategoryResult
            {
                Category = "Email",
                SubCategory = "Email Address",
                SuggestedCategory = "Email address",
                IsAiCategorized = false
            };
        }

        if(LooksLikeCode(content))
        {
            return new ClipboardCategoryResult
            {
                Category = "Text",
                SubCategory = "Code",
                SuggestedCategory = "Code snippet",
                IsAiCategorized = false

            };
        }
        if (LooksLikeCodeComment(content))
        {
            return new ClipboardCategoryResult
            {
                Category = "Code",
                SubCategory = "Code Comment",
                SuggestedCategory = "Code comment",
                IsAiCategorized = false
            };
        }

        return new ClipboardCategoryResult
        {
            Category = "Text",
            SubCategory = "General",
            SuggestedCategory = "General text",
            IsAiCategorized = false
        };



    }

}
