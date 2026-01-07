namespace Model
{
	public class EnumDef
	{
        public enum NotifyType
        {
            UNKNOWN, SYSTEM, ORDER, STG, SAVE, BALANCE, CHARGE, REMOTE_CALL, MAX
        }
        public enum Period
        {
            TIME_1S, TIME_1M = 60, TIME_5M = 60 * 5, TIME_15M = 60 * 15, TIME_30M = 60 * 30, TIME_1H = 60 * 60, TIME_2H = 2 * 60 * 60, TIME_4H = 60 * 60 * 4, TIME_1D = 60 * 60 * 24, TIME_UNKNOWN
        }

        public enum OrderType
        {
            NONE, BUY, SELL, BUY_TO_COVER, SELL_TO_COVER
        }

        public enum PlotType
        {
            LINE, CURVE, RECTANGLE, XLINE, POINT, TEXT, LINE_SEGMENT
        }
    }
}
