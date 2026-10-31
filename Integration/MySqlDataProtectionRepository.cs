using Microsoft.AspNetCore.DataProtection.Repositories;
using MySqlConnector;
using System.Xml.Linq;

namespace Nutragea.Api.Security;

public sealed class MySqlDataProtectionRepository(
    string connectionString) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();

        using var connection = new MySqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT xml_value
            FROM app_data_protection_keys
            ORDER BY id
            """;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = new MySqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_data_protection_keys
                (friendly_name, xml_value)
            VALUES
                (@name, @xml)
            """;

        command.Parameters.AddWithValue("@name", friendlyName);
        command.Parameters.AddWithValue(
            "@xml",
            element.ToString(SaveOptions.DisableFormatting));

        command.ExecuteNonQuery();
    }
}