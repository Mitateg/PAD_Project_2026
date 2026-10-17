using System.Text.Json;
using Pad.Common;
using Pad.Common.Grpc;
using Xunit;

namespace Pad.Contract.Tests;

public class ExamplesTests
{
     private static string ExamplesDir =>
         Path.Combine(AppContext.BaseDirectory, "examples");

     public static IEnumerable<object[]> ValidFiles() =>
         Directory.GetFiles(ExamplesDir, "valid-*.json").Select(f => new object[] { Path.GetFileName(f) });

     public static IEnumerable<object[]> InvalidFiles() =>
         Directory.GetFiles(ExamplesDir, "invalid-*.json").Select(f => new object[] { Path.GetFileName(f) });

     /// <summary>
     /// Parseaza JSON-ul dintr-un fisier example intr-un BrokerMessage.
     /// Returneaza null daca JSON-ul nu e valid.
     /// </summary>
     private static BrokerMessage? ParseExample(string json)
     {
          try
          {
               using var doc = JsonDocument.Parse(json);
               var root = doc.RootElement;

               var message = new BrokerMessage();

               if (root.TryGetProperty("messageId", out var mid))
                    message.MessageId = mid.GetString() ?? "";
               if (root.TryGetProperty("correlationId", out var cid))
                    message.CorrelationId = cid.GetString() ?? "";
               if (root.TryGetProperty("messageType", out var mt))
                    message.MessageType = mt.GetString() ?? "";
               if (root.TryGetProperty("schemaVersion", out var sv))
                    message.SchemaVersion = sv.GetInt32();
               if (root.TryGetProperty("occurredAt", out var oa))
                    message.OccurredAt = oa.GetString() ?? "";
               if (root.TryGetProperty("payload", out var pl))
                    message.PayloadJson = pl.ToString();

               return message;
          }
          catch (JsonException)
          {
               return null;
          }
     }

     [Theory]
     [MemberData(nameof(ValidFiles))]
     public void ExempleleValide_TrecValidarea(string fileName)
     {
          string json = File.ReadAllText(Path.Combine(ExamplesDir, fileName)).Trim();
          var message = ParseExample(json);

          Assert.NotNull(message);
          string? reason = MessageValidator.Validate(message!);

          Assert.Null(reason);
     }

     [Theory]
     [MemberData(nameof(InvalidFiles))]
     public void ExempleleInvalide_PicaValidareaCuMotivulDinNume(string fileName)
     {
          // Numele fisierului: invalid-<motiv>.json, de ex. invalid-missing_field-messageType.json
          // => motiv asteptat: missing_field:messageType
          string expectedReason = Path.GetFileNameWithoutExtension(fileName)
              .Substring("invalid-".Length)
              .Replace("-", ":");

          string json = File.ReadAllText(Path.Combine(ExamplesDir, fileName)).Trim();
          var message = ParseExample(json);

          if (expectedReason == "invalid_json")
          {
               // Fisierul contine JSON invalid, ParseExample returneaza null
               Assert.Null(message);
               return;
          }

          Assert.NotNull(message);
          string? reason = MessageValidator.Validate(message!);

          Assert.False(reason == null, $"{fileName} ar fi trebuit sa fie invalid");
          Assert.Equal(expectedReason, reason);
     }
}