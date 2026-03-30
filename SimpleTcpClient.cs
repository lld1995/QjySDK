using Common;
using Model;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace QjySDK.Stg
{
    public class SimpleTcpClient
    {
        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private bool _isConnected;
        private bool _disposed;
        private StgBase _sb;
        private string? _ip;
        private int _port;
        private string? _startMessage;
        private Dictionary<string, TableUnit> _tuDic = new Dictionary<string, TableUnit>();
        private Dictionary<string, TaskCompletionSource<Symbol>> _pendingGetSymbol = new Dictionary<string, TaskCompletionSource<Symbol>>();
        private Dictionary<string, string> _lastFinalBarHashDic = new Dictionary<string, string>();
        private readonly Channel<List<RemoteCall>> _rcChannel = Channel.CreateUnbounded<List<RemoteCall>>();

        private static readonly int[] ReconnectDelays = { 3000, 5000, 10000, 30000 };

        public SimpleTcpClient(StgBase sb)
        {
            _sb = sb;
            var t = new Thread(() => RcConsumerLoop().Wait())
            {
                IsBackground = true,
                Name = "RcConsumer"
            };
            t.Start();
        }

        public void SetStartMessage(string message)
        {
            _startMessage = message;
        }

        public async Task ConnectAsync(string ip,int port)
        {
            _ip = ip;
            _port = port;
            await ConnectInternalAsync();
        }

        private async Task ConnectInternalAsync()
        {
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(_ip!, _port);

                var stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8)
                {
                    AutoFlush = true
                };

                _isConnected = true;
                Console.WriteLine($"已连接到服务器 {_ip}:{_port}");

                // 启动接收消息任务
                _ = Task.Run(ReceiveMessagesAsync);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"连接失败: {ex.Message}");
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            try
            {
                while (_isConnected && _client?.Connected == true)
                {
                    var message = await _reader!.ReadLineAsync();
                    if (message == null)
                    {
                        Console.WriteLine("连接已断开");
                        Disconnect();
                        break;
                    }
                    var dic = message.ToJsonObj<Dictionary<string, object>>();
                    var oper = dic["oper"].ToString();
                    if (oper == "user")
                    {
                        var nt = (EnumDef.NotifyType)int.Parse(dic["nt"].ToString());
                        if (nt == EnumDef.NotifyType.REMOTE_CALL)
                        {
                            var rcList = dic["data"].ToString().ToJsonObj<List<RemoteCall>>();
                            await _rcChannel.Writer.WriteAsync(rcList);
                        }
                        else if (nt == EnumDef.NotifyType.STG)
                        {
                            var stgId = dic["id"].ToString();
                            if (stgId == _sb.Id)
                            {
                                int status = int.Parse(dic["data"].ToString());
                                _sb.IsBacktest = status == 1;
                            }
                        }
                        else if (nt == EnumDef.NotifyType.STG)
                        {
                            var stgId = dic["id"].ToString();
                            if (stgId == _sb.Id)
                            {
                                int status = int.Parse(dic["data"].ToString());
                                _sb.IsBacktest = status == 1;
                            }
                        }
                    }
                    else if (oper == "getSymbol")
                    {
                        var s=((JsonElement)dic["symbolExtra"]).ToJsonObj<Symbol>();
                        s.symbol_type = (int)Enum.Parse<EnumDef.SymbolType>(dic["st"].ToString());
                        var mktSymbol = dic["mktSymbol"].ToString();
                        if (_pendingGetSymbol.TryGetValue(mktSymbol, out var tcs))
                        {
                            _pendingGetSymbol.Remove(mktSymbol);
                            tcs.SetResult(s);
                        }
                    }

                    Console.WriteLine($"{message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"接收消息时发生错误: {ex.Message}");
                Disconnect();
            }
        }


        public TaskCompletionSource<Symbol> RegisterGetSymbol(string mktSymbol)
        {
            var tcs = new TaskCompletionSource<Symbol>();
            _pendingGetSymbol[mktSymbol] = tcs;
            return tcs;
        }

        public async Task SendMessageAsync(string message)
        {
            if (!_isConnected || _writer == null)
            {
                Console.WriteLine("未连接，消息丢弃");
                return;
            }
            try
            {
                await _writer.WriteLineAsync(message);
            }
            catch
            {
                Console.WriteLine("发送消息失败");
                Disconnect();
            }
        }

        private void Disconnect()
        {
            if (!_isConnected) return;

            _isConnected = false;
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _reader = null;
            _writer = null;
            _client = null;

            // cancel all pending GetSymbol requests
            foreach (var kv in _pendingGetSymbol)
                kv.Value.TrySetCanceled();
            _pendingGetSymbol.Clear();

            Console.WriteLine("已断开连接");

            if (!_disposed)
                _ = Task.Run(ReconnectAsync);
        }

        private async Task ReconnectAsync()
        {
            int attempt = 0;
            while (!_disposed && !_isConnected)
            {
                int delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];
                Console.WriteLine($"将在 {delay / 1000} 秒后尝试重连 (第{attempt + 1}次)...");
                await Task.Delay(delay);

                if (_disposed) break;

                try
                {
                    await ConnectInternalAsync();
                    if (_isConnected)
                    {
                        Console.WriteLine("重连成功");
                        if (!string.IsNullOrEmpty(_startMessage))
                        {
                            await SendMessageAsync(_startMessage);
                            Console.WriteLine("已重新注册策略");
                        }
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"重连失败: {ex.Message}");
                }
                attempt++;
            }
        }

        public void Close()
        {
            _disposed = true;
            _rcChannel.Writer.TryComplete();
            if (_isConnected)
            {
                _isConnected = false;
                try { _reader?.Dispose(); } catch { }
                try { _writer?.Dispose(); } catch { }
                try { _client?.Close(); } catch { }
            }
            Console.WriteLine("客户端已关闭");
        }

        private async Task RcConsumerLoop()
        {
            await foreach (var rcList in _rcChannel.Reader.ReadAllAsync())
            {
                try
                {
                    foreach (var rc in rcList)
                    {
                        if (rc.Name == "OnBar")
                        {
                            var mktSymbol = rc.ArgList[0].ToString();
                            var period = Enum.Parse<EnumDef.Period>(rc.ArgList[1].ToString());
                            var q = ((JsonElement)rc.ArgList[2]).ToJsonObj<SkQuote>();
                            var isFinal = ((JsonElement)rc.ArgList[3]).GetBoolean();
                            var tableName = Tools.GetTableName(Tools.GetSP(mktSymbol, period));
                            TableUnit tu = null;
                            if (_tuDic.ContainsKey(tableName))
                            {
                                tu = _tuDic[tableName];
                            }
                            else
                            {
                                tu = new TableUnit();
                                tu.QuoteList = new List<SkQuote>();
                                tu.MktSymbol = mktSymbol;
                                tu.Period = period;
                                _tuDic[tableName] = tu;
                            }
                            if (isFinal)
                            {
                                var qHash = BuildFinalBarHash(mktSymbol, period, isFinal, q);
                                if (_lastFinalBarHashDic.TryGetValue(tableName, out var lastHash) && lastHash == qHash)
                                {
                                    continue;
                                }
                                _lastFinalBarHashDic[tableName] = qHash;
                                tu.QuoteList.Add(q);
                            }
                            _sb.OnBar(period, tu, isFinal, q);
                        }
                        else if (rc.Name == "OnGlobalIndicator")
                        {
                            _sb.OnGlobalIndicator(_tuDic.Values.ToList());
                        }
                        else if (rc.Name == "OnPeriodEnd")
                        {
                            var mktSymbol = rc.ArgList[0].ToString();
                            var period = Enum.Parse<EnumDef.Period>(rc.ArgList[1].ToString());
                            var q = ((JsonElement)rc.ArgList[2]).ToJsonObj<SkQuote>();
                            _sb.OnPeriodEnd(period, q, mktSymbol);
                        }
                        else if (rc.Name == "OnSendOrder")
                        {
                            var mktSymbol = rc.ArgList[0].ToString();
                            var period = Enum.Parse<EnumDef.Period>(rc.ArgList[1].ToString());
                            var price = ((JsonElement)rc.ArgList[2]).GetDecimal();
                            var tableName = Tools.GetTableName(Tools.GetSP(mktSymbol, period));
                            TableUnit tu = null;
                            if (_tuDic.ContainsKey(tableName))
                            {
                                tu = _tuDic[tableName];
                                _sb.OnSendOrder(tu, price);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
                finally
                {
                    await _sb.PushAndClear();
                }
            }
        }

        private static string BuildFinalBarHash(string mktSymbol, EnumDef.Period period, bool isFinal, SkQuote q)
        {
            var raw = string.Join("|",
                mktSymbol,
                period,
                isFinal,
                q.Date.ToString("O"),
                q.Open,
                q.High,
                q.Low,
                q.Close,
                q.Volume,
                q.Amount);
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes);
        }
    }
}
