using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WinOldRecovery.Recipes;

internal static class SqliteExports
{
    public static (int Count, string Html, string Csv) ChromiumHistory(string historyCopy)
    {
        using SqliteConnection connection = ReadOnlySqlite.OpenReadOnly(historyCopy);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT url, COALESCE(title, ''), visit_count
            FROM urls
            WHERE IFNULL(hidden, 0) = 0
            ORDER BY last_visit_time DESC
            LIMIT 5000;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder html = new();
        StringBuilder csv = new();
        html.AppendLine("<!DOCTYPE html><title>History</title><h1>History</h1><ul>");
        csv.AppendLine("url,title,visit_count");
        int count = 0;
        while (reader.Read())
        {
            string url = reader.GetString(0);
            string title = reader.GetString(1);
            long visits = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            html.Append("<li><a href=\"")
                .Append(WebUtility.HtmlEncode(url))
                .Append("\">")
                .Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(title) ? url : title))
                .AppendLine("</a></li>");
            csv.Append(Csv(url)).Append(',').Append(Csv(title)).Append(',').Append(visits).AppendLine();
            count++;
        }

        html.AppendLine("</ul>");
        return (count, html.ToString(), csv.ToString());
    }

    public static (int Count, string Html) FirefoxBookmarks(string placesCopy)
    {
        using SqliteConnection connection = ReadOnlySqlite.OpenReadOnly(placesCopy);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(p.url, ''), COALESCE(b.title, p.title, p.url, '')
            FROM moz_bookmarks b
            JOIN moz_places p ON p.id = b.fk
            WHERE b.type = 1 AND IFNULL(p.url, '') <> ''
            LIMIT 5000;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder html = new();
        html.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        html.AppendLine("<TITLE>Bookmarks</TITLE><H1>Bookmarks</H1><DL><p>");
        int count = 0;
        while (reader.Read())
        {
            string url = reader.GetString(0);
            string title = reader.GetString(1);
            html.Append("<DT><A HREF=\"")
                .Append(WebUtility.HtmlEncode(url))
                .Append("\">")
                .Append(WebUtility.HtmlEncode(title))
                .AppendLine("</A>");
            count++;
        }

        html.AppendLine("</DL><p>");
        return (count, html.ToString());
    }

    public static string FirefoxHistoryCsv(string placesCopy)
    {
        using SqliteConnection connection = ReadOnlySqlite.OpenReadOnly(placesCopy);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT url, COALESCE(title, '')
            FROM moz_places
            WHERE IFNULL(hidden, 0) = 0 AND IFNULL(url, '') <> ''
            LIMIT 5000;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        StringBuilder csv = new();
        csv.AppendLine("url,title");
        while (reader.Read())
        {
            csv.Append(Csv(reader.GetString(0))).Append(',').Append(Csv(reader.GetString(1))).AppendLine();
        }

        return csv.ToString();
    }

    public static (int Count, string Csv) ChromiumAutofill(string webDataCopy)
    {
        using SqliteConnection connection = ReadOnlySqlite.OpenReadOnly(webDataCopy);
        StringBuilder csv = new();
        csv.AppendLine("kind,name,value");
        int count = 0;
        count += AppendAutofillTable(
            connection,
            csv,
            "SELECT name, value FROM autofill LIMIT 5000",
            "field");
        count += AppendAutofillTable(
            connection,
            csv,
            """
            SELECT 'address', TRIM(COALESCE(street_address, '') || ' ' || COALESCE(city, '') || ' ' || COALESCE(zipcode, ''))
            FROM autofill_profiles
            LIMIT 500
            """,
            "profile");
        return (count, csv.ToString());
    }

    private static int AppendAutofillTable(
        SqliteConnection connection,
        StringBuilder csv,
        string sql,
        string kind)
    {
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            using SqliteDataReader reader = command.ExecuteReader();
            int count = 0;
            while (reader.Read())
            {
                string name = reader.GetString(0);
                string value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                csv.Append(Csv(kind)).Append(',').Append(Csv(name)).Append(',').Append(Csv(value)).AppendLine();
                count++;
            }

            return count;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
