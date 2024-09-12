using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;

[assembly: AssemblyVersion("0.3.3")]
[assembly: AssemblyFileVersion("0.3.3")]

namespace SakuraTranslator
{
    public class SakuraTranslatorEndpoint : ITranslateEndpoint
    {
        public string Id => "SakuraTranslator";

        public string FriendlyName => "Sakura Translator";

        public int MaxConcurrency => _maxConcurrency;

        public int MaxTranslationsPerRequest => 1;

        // params
        private string _endpoint;
        private string _apiType;
        private int _maxConcurrency;
        private bool _useDict;
        private string _dictMode;
        private Dictionary<string, List<string>> _dict;

        // local var
        private string _fullDictStr;

        public void Initialize(IInitializationContext context)
        {
            _endpoint = context.GetOrCreateSetting<string>("Sakura", "Endpoint", "http://127.0.0.1:8080/completion");
            _apiType = context.GetOrCreateSetting<string>("Sakura", "ApiType", string.Empty);
            if (!int.TryParse(context.GetOrCreateSetting<string>("Sakura", "MaxConcurrency", "1"), out _maxConcurrency))
            {
                _maxConcurrency = 1;
            }
            if (_maxConcurrency > ServicePointManager.DefaultConnectionLimit)
            {
                ServicePointManager.DefaultConnectionLimit = _maxConcurrency;
            }
            if (!bool.TryParse(context.GetOrCreateSetting<string>("Sakura", "UseDict", string.Empty), out _useDict))
            {
                _useDict = false;
            }
            _dictMode = context.GetOrCreateSetting<string>("Sakura", "DictMode", "Full");
            var dictStr = context.GetOrCreateSetting<string>("Sakura", "Dict", string.Empty);
            if (!string.IsNullOrEmpty(dictStr))
            {
                try
                {
                    _dict = new Dictionary<string, List<string>>();
                    JObject dictJObj = JsonConvert.DeserializeObject(dictStr) as JObject;
                    foreach (var item in dictJObj)
                    {
                        try
                        {
                            var vArr = JArray.Parse(item.Value.ToString());
                            List<string> vList;
                            if (vArr.Count <= 0)
                            {
                                throw new Exception();
                            }
                            else if (vArr.Count == 1)
                            {
                                vList = new List<string> { vArr[0].ToString(), string.Empty };
                            }
                            else
                            {
                                vList = new List<string> { vArr[0].ToString(), vArr[1].ToString() };
                            }
                            _dict.Add(item.Key, vList);
                        }
                        catch
                        {
                            _dict.Add(item.Key, new List<string> { item.Value.ToString(), string.Empty });
                        }
                    }
                    if (_dict.Count == 0)
                    {
                        _useDict = false;
                        _fullDictStr = string.Empty;
                    }
                    else
                    {
                        var dictStrings = GetDictStringList(_dict);
                        _fullDictStr = string.Join("\n", dictStrings.ToArray());
                    }
                }
                catch
                {
                    _useDict = false;
                    _fullDictStr = string.Empty;
                }
            }
        }

        private List<string> GetDictStringList(IEnumerable<KeyValuePair<string, List<string>>> kvPairs)
        {
            List<string> dictList = new List<string>();
            foreach (var entry in kvPairs)
            {
                var src = entry.Key;
                var dst = entry.Value[0];
                var info = entry.Value[1];
                if (string.IsNullOrEmpty(info))
                {
                    dictList.Add($"{src}->{dst}");
                }
                else
                {
                    dictList.Add($"{src}->{dst} #{info}");
                }
            }

            return dictList;
        }

        public IEnumerator Translate(ITranslationContext context)
        {
            var untranslatedText = context.UntranslatedText;

            // 以换行符分割文本
            string[] lines = untranslatedText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            StringBuilder translatedTextBuilder = new StringBuilder();

            foreach (var line in lines)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    // 逐行翻译
                    IEnumerator translateLineCoroutine = TranslateLine(line, translatedTextBuilder);
                    while (translateLineCoroutine.MoveNext())
                    {
                        yield return null;
                    }
                }
                else
                {
                    // 保留空行
                    translatedTextBuilder.AppendLine();
                }
            }

            string translatedText = translatedTextBuilder.ToString().TrimEnd('\r', '\n');
            context.Complete(translatedText);
        }

        private static string ConvertToTraditionalChinese(string simplifiedChineseText)
        {
            string url = "https://api.zhconvert.org/convert?text=" + simplifiedChineseText + "&converter=Taiwan";

            HttpWebRequest conversionRequest = (HttpWebRequest) WebRequest.Create(url);
            conversionRequest.Method = "GET";

            WebResponse conversionResponse = conversionRequest.GetResponse();

            using(Stream stream = conversionResponse.GetResponseStream())
            {
                using(StreamReader reader = new StreamReader(stream))
                {
                    String conversionResult = reader.ReadToEnd();
                    JObject jsonConversionResult = JObject.Parse(conversionResult);
                    return jsonConversionResult["data"]["text"].ToString();
                }
            }
        }

        private IEnumerator TranslateLine(string line, StringBuilder translatedTextBuilder)
        {
            // 构建请求JSON
            string json = MakeRequestJson(line);
            var dataBytes = Encoding.UTF8.GetBytes(json);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(_endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.ContentLength = dataBytes.Length;

            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(dataBytes, 0, dataBytes.Length);
            }

            var asyncResult = request.BeginGetResponse(null, null);

            // 等待异步操作完成
            while (!asyncResult.IsCompleted)
            {
                yield return null;
            }

            string responseText;
            using (WebResponse response = request.EndGetResponse(asyncResult))
            {
                using (Stream responseStream = response.GetResponseStream())
                {
                    using (StreamReader reader = new StreamReader(responseStream))
                    {
                        responseText = reader.ReadToEnd();
                    }
                }
            }

            responseText = ConvertToTraditionalChinese(responseText);

            // 手动解析JSON响应
            var startIndex = responseText.IndexOf("\"content\":") + 10;
            var endIndex = responseText.IndexOf(",", startIndex);
            var translatedLine = responseText.Substring(startIndex, endIndex - startIndex).Trim('\"', ' ', '\r', '\n');
            if (translatedLine.EndsWith("<|im_end|>"))
            {
                translatedLine = translatedLine.Substring(0, translatedLine.Length - "<|im_end|>".Length);
            }
            if (translatedLine.EndsWith("。") && !line.Trim().EndsWith("。"))
            {
                translatedLine = translatedLine.Substring(0, translatedLine.Length - "。".Length);
            }
            if (translatedLine.EndsWith("。」") && !line.Trim().EndsWith("。」"))
            {
                translatedLine = translatedLine.Substring(0, translatedLine.Length - "。」".Length) + "」";
            }

            // 将翻译后的行添加到StringBuilder
            translatedTextBuilder.AppendLine(translatedLine);
        }

        private string MakeRequestJson(string line)
        {
            string json;
            if (_apiType == "Qwen")
            {
                json = $"{{\"prompt\":\"<|im_start|>system\\n你是輕小說翻譯模型，可以流暢通順地以日本輕小說的風格將日文翻譯成繁體中文，" +
                $"並聯繫上下文正確使用人稱代名詞，不擅自添加原文中沒有的代名詞。<|im_end|>\\n<|im_start|>user\\n將下面的日文文字翻譯成中文：" +
                $"{EscapeJsonString(line)}<|im_end|>\\n<|im_start|>assistant\\n\",\"n_predict\":1024,\"temperature\":0.1,\"top_p\":0.3,\"repeat_penalty\":1," +
                $"\"frequency_penalty\":0.2,\"top_k\":40,\"seed\":-1}}";
            }
            else if (_apiType == "OpenAI")
            {
                json = MakeOpenAIPrompt(line);
            }
            else if (_apiType == "Sakura32bV010")
            {
                json = MakeSakura32bV010Prompt(line);
            }
            else
            {
                json = $"{{\"frequency_penalty\": 0.2, \"n_predict\": 1000, \"prompt\": \"<reserved_106>將下面的日文文字翻譯成中文：{EscapeJsonString(line)}<reserved_107>\", \"repeat_penalty\": 1, \"temperature\": 0.1, \"top_k\": 40, \"top_p\": 0.3}}";
            }

            return json;
        }

        private string MakeOpenAIPrompt(string line)
        {
            string messagesStr = string.Empty;
            if (_useDict)
            {
                var messages = new List<PromptMessage>
                {
                    new PromptMessage
                    {
                        Role = "system",
                        Content = "你是一個輕小說翻譯模型，可以流暢通順地以日本輕小說的風格將日文翻譯成繁體中文，並聯繫上下文正確使用人稱代詞，注意不要擅自添加原文中沒有的代詞，也不要擅自增加或減少換行。"
                    }
                };
                string dictStr;
                if (_dictMode == "Full")
                {
                    dictStr = _fullDictStr;
                }
                else
                {
                    var usedDict = _dict.Where(x => line.Contains(x.Key));
                    if (usedDict.Count() > 0)
                    {
                        var dictStrings = GetDictStringList(usedDict);
                        dictStr = string.Join("\n", dictStrings.ToArray());
                    }
                    else
                    {
                        dictStr = string.Empty;
                    }
                }
                if (string.IsNullOrEmpty(dictStr))
                {
                    messages.Add(new PromptMessage
                    {
                        Role = "user",
                        Content = $"將下面的日文文字翻譯成中文：{line}"
                    });
                }
                else
                {
                    messages.Add(new PromptMessage
                    {
                        Role = "user",
                        Content = $"根據以下術語表：\n{dictStr}\n將下面的日文文本根據上述術語表的對應關係和註釋翻譯成中文：{line}"
                    });
                }
                messagesStr = SerializePromptMessages(messages);
            }
            else
            {
                messagesStr = "[" +
                       $"{{" +
                       $"\"role\": \"system\"," +
                       $"\"content\": \"你是一個輕小說翻譯模型，可以流暢通順地以日本輕小說的風格將日文翻譯成繁體中文，並聯繫上下文正確使用人稱代詞，注意不要擅自添加原文中沒有的代詞，也不要擅自增加或減少換行。\"" +
                       $"}}," +
                                $"{{" +
                                $"\"role\": \"user\"," +
                       $"\"content\": \"將下面的日文文字翻譯成中文：{EscapeJsonString(line)}\"" +
                       $"}}" +
                       $"]";
            }
            return $"{{" +
                       $"\"model\": \"sukinishiro\"," +
                       $"\"messages\": " +
                       messagesStr +
                       $"," +
                       $"\"temperature\": 0.1," +
                       $"\"top_p\": 0.3," +
                       $"\"max_tokens\": 1000," +
                       $"\"frequency_penalty\": 0.2," +
                       $"\"do_sample\": false," +
                       $"\"top_k\": 40," +
                       $"\"um_beams\": 1," +
                       $"\"repetition_penalty\": 1.0" +
                       $"}}";
        }

        private string MakeSakura32bV010Prompt(string line)
        {
            string messagesStr = string.Empty;
            var messages = new List<PromptMessage>
                {
                    new PromptMessage
                    {
                        Role = "system",
                        Content = "你是一個輕小說翻譯模型，可以流暢通順地使用給定的術語表以日本輕小說的風格將日文翻譯成繁體中文，並聯繫上下文正確使用人稱代詞，注意不要混淆使役態和被動態的主語和受詞，不要擅自添加原文中沒有的代名詞，也不要擅自增加或減少換行。"
                    }
                };
            string dictStr;
            if (_useDict == false)
            {
                dictStr = string.Empty;
            }
            else if (_dictMode == "Full")
            {
                dictStr = _fullDictStr;
            }
            else
            {
                var usedDict = _dict.Where(x => line.Contains(x.Key));
                if (usedDict.Count() > 0)
                {
                    var dictStrings = GetDictStringList(usedDict);
                    dictStr = string.Join("\n", dictStrings.ToArray());
                }
                else
                {
                    dictStr = string.Empty;
                }
            }
            messages.Add(new PromptMessage
            {
                Role = "user",
                Content = $"根據以下術語表（可以為空）：\n{dictStr}\n\n" +
                          $"將下面的日文文本根據上述術語表的對應關係和備註翻譯成中文：{line}"
            });
            messagesStr = SerializePromptMessages(messages);

            return $"{{" +
                       $"\"model\": \"sukinishiro\"," +
                       $"\"messages\": " +
                       messagesStr +
                       $"," +
                       $"\"temperature\": 0.1," +
                       $"\"top_p\": 0.3," +
                       $"\"max_tokens\": 1000," +
                       $"\"frequency_penalty\": 0.2," +
                       $"\"do_sample\": false," +
                       $"\"top_k\": 40," +
                       $"\"um_beams\": 1," +
                       $"\"repetition_penalty\": 1.0" +
                       $"}}";
        }

        private string SerializePromptMessages(List<PromptMessage> messages)
        {
            string result = "[";
            result += string.Join(",", messages.Select(x => $"{{\"role\":\"{x.Role}\"," +
                $"\"content\":\"{EscapeJsonString(x.Content)}\"}}").ToArray());
            result += "]";
            return result;
        }

        private string EscapeJsonString(string str)
        {
            return str
                .Replace("\\", "\\\\")
                .Replace("/", "\\/")
                .Replace("\b", "\\b")
                .Replace("\f", "\\f")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t")
                .Replace("\v", "\\v")
                .Replace("\"", "\\\"");
        }

        class PromptMessage
        {
            public string Role { get; set; }
            public string Content { get; set; }
        }
    }
}
