using Common;
using Model;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace QjySDK
{
    public class SimpleTcpClient
    {
        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private bool _isConnected;
        private StgBase _sb;
        private Dictionary<string, TableUnit> _tuDic = new Dictionary<string, TableUnit>();

        public SimpleTcpClient(StgBase sb)
        {
            _sb = sb;
        }


        public async Task ConnectAsync(string ip,int port)
        {
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(ip, port);

                var stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8)
                {
                    AutoFlush = true
                };

                _isConnected = true;
                Console.WriteLine($"已连接到服务器 {ip}:{port}");

                // 启动接收消息任务
                _ = Task.Run(ReceiveMessagesAsync);

                // 启动发送消息任务
                //await SendMessagesAsync();
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
                                        tu=_tuDic[tableName];
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
                                        tu.QuoteList.Add(q);
                                    }
                                    _sb.OnBar(period, tu, isFinal, q);
                                }
                                else if(rc.Name== "OnGlobalIndicator")
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
                            }

                            await _sb.PushAndClear();
                        }
                    }

                    Console.WriteLine($"{message}");
                }
            }
            catch
            {
                Console.WriteLine("接收消息时发生错误");
                Disconnect();
            }
        }


        public async Task SendMessageAsync(string message)
        {
            try
            {
                await _writer!.WriteLineAsync(message);
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
            _reader?.Dispose();
            _writer?.Dispose();
            _client?.Close();
            Console.WriteLine("已断开连接");
        }
    }
}
