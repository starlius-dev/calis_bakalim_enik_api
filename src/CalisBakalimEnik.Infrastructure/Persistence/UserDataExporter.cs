using System.IO.Compression;
using System.Text;
using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// Builds the "Verilerimi indir" ZIP (D16): one JSON file per table in
/// <see cref="UserDataMap"/>, a profile.json and a README.
/// </summary>
/// <remarks>
/// Postgres renders the JSON itself (<c>to_jsonb</c> minus the hidden
/// columns), so a column added to a table shows up in the export without a
/// code change here, and the hidden list is the only way to keep one out.
/// Built in memory: the whole of one person's data is kilobytes to a few MB.
/// </remarks>
public sealed class UserDataExporter(AppDbContext db, IClock clock)
{
    public async Task<byte[]> ExportAsync(Guid userId, string appVersion, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        try
        {
            using var buffer = new MemoryStream();

            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var profile = await ScalarAsync(connection, UserDataMap.ProfileSql, userId, null, ct);
                await AddAsync(zip, "profil.json", profile ?? "{}", ct);

                var files = new List<string>();
                foreach (var table in UserDataMap.Tables.Where(t => t.Export))
                {
                    var json = await ScalarAsync(
                        connection, UserDataMap.ExportSql(table), userId, table.Hidden, ct);

                    var path = $"{table.Area}/{table.Table}.json";
                    await AddAsync(zip, path, json ?? "[]", ct);
                    files.Add(path);
                }

                await AddAsync(zip, "BENIOKU.txt", Readme(files, appVersion), ct);
            }

            return buffer.ToArray();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string?> ScalarAsync(
        NpgsqlConnection connection, string sql, Guid userId, string[]? hidden, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("uid", userId);
        if (hidden is not null)
            command.Parameters.Add(new NpgsqlParameter("hidden", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                Value = hidden,
            });

        return await command.ExecuteScalarAsync(ct) as string;
    }

    private static async Task AddAsync(ZipArchive zip, string path, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
    }

    private string Readme(IEnumerable<string> files, string appVersion) =>
        $"""
         Çalış Bakalım Enik: verilerin
         =============================

         Bu arşiv, hesabına ait saklanan tüm verileri içerir.
         Oluşturulma: {clock.UtcNow:yyyy-MM-dd HH:mm} (UTC)
         Sürüm: {appVersion}

         Dosyalar JSON biçimindedir; her dosya bir tablodaki kayıtlarındır.
         Silinmiş olarak işaretlenmiş ama henüz kalıcı olarak silinmemiş
         kayıtlar da dahildir (deleted_at alanı dolu olanlar).

         Güvenlik nedeniyle bazı alanlar bilerek dışarıda bırakıldı:
         şifrenin özeti, iki adımlı doğrulama anahtarları, yedek kodların
         özetleri, oturum anahtarları ve bildirim cihaz anahtarları.

         profil.json   hesap bilgilerin
         plan/         dönemler, dersler, projeler, görevler, notlar, takvim, odak
         saglik/       ilaçlar ve dozlar, antrenmanlar, öğünler, ölçümler, hedefler
         hesap/        bildirimler, cihazlar, oturumlar, giriş ve güvenlik geçmişi

         {string.Join(Environment.NewLine + " ", files)}
         """;
}
