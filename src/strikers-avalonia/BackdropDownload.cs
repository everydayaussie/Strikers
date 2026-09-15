using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace Strikers.App;

internal readonly record struct Picture(string Url, long Bytes, string Sha256, string Target);

internal static class BackdropDownload
{
    private const int Piece = 64 * 1024;

    public static string Target()
    {
        return Path.Combine(AppContext.BaseDirectory, BackdropSource.FileName);
    }

    public static Picture Backdrop()
    {
        return new Picture(BackdropSource.Url, BackdropSource.Bytes, BackdropSource.Sha256, Target());
    }

    public static Picture Icon()
    {
        return new Picture(IconSource.Url, IconSource.Bytes, IconSource.Sha256, IconFile.Target());
    }

    public static async Task<string?> FetchAsync(Picture picture, Action<long> progress, CancellationToken cancel)
    {
        var target = picture.Target;
        var part = target + ".part";

        if (target == Target() && BackdropFile.AnyBesideExe())
        {
            return "a backdrop is already beside the launcher";
        }

        if (File.Exists(target))
        {
            return "that file is already beside the launcher";
        }

        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                AutomaticDecompression = DecompressionMethods.None,
            };
            using var http = new HttpClient(handler);
            http.Timeout = TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Strikers/1.0");

            using var response = await http.GetAsync(picture.Url,
                                                     HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return $"the host answered {(int)response.StatusCode}";
            }

            var announced = response.Content.Headers.ContentLength;
            if (announced is { } length && length != picture.Bytes)
            {
                return $"the host offers {length} bytes, not the {picture.Bytes} expected";
            }

            DeleteQuietly(part);

            long got = 0;
            var head = new byte[BackdropSource.PngSignature.Length];
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var body = await response.Content.ReadAsStreamAsync(cancel))
            await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[Piece];
                while (true)
                {
                    var read = await Streams.ReadWithinAsync(body, buffer, BackdropSource.QuietWithin, cancel);
                    if (read == 0)
                    {
                        break;
                    }

                    if (got + read > picture.Bytes)
                    {
                        return $"the host sent more than the {picture.Bytes} bytes expected";
                    }

                    for (var i = 0; i < read && got + i < head.Length; i++)
                    {
                        head[(int)got + i] = buffer[i];
                    }

                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                    got += read;
                    progress(got);
                }
            }

            var problem = BackdropSource.Problem(got, BackdropSource.HexOf(sha.GetHashAndReset()), head,
                                                 picture.Bytes, picture.Sha256);
            if (problem is not null)
            {
                return problem;
            }

            File.Move(part, target, overwrite: false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return cancel.IsCancellationRequested ? "cancelled" : "the host stopped answering";
        }
        catch (TimeoutException)
        {
            return "the host stopped answering";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
        finally
        {
            DeleteQuietly(part);
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
