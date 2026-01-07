using System.Collections.Generic;

namespace Model
{
    public class StgDesc
    {
        private Dictionary<string, object> _argDic = new Dictionary<string, object>();
        public Dictionary<string, object> ArgDic { get => _argDic; set => _argDic = value; }

        public int MaxSymbolNum { get; set; }

        public int UseGlobalCalc { get; set; }

        public int SubChartNum { get; set; }

        private Dictionary<string, string> _colorDic = new Dictionary<string, string>();
        public Dictionary<string, string> ColorDic { get => _colorDic; set => _colorDic = value; }

        private Dictionary<string, int> _midValDic = new Dictionary<string, int>();
        public Dictionary<string, int> MidValDic { get => _midValDic; set => _midValDic = value; }
    }
}
