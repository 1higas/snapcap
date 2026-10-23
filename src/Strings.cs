using System.Globalization;

namespace ChargeLimit
{
    // English by default; Portuguese when the Windows display language is Portuguese.
    static class Strings
    {
        static readonly bool Pt = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";

        public static readonly string Title = Pt ? "Limite de carga" : "Charge Limit";
        public static readonly string EnableLimit = Pt ? "Limitar a carga da bateria" : "Limit battery charge";
        public static readonly string StopAt = Pt ? "Parar de carregar em" : "Stop charging at";
        public static readonly string ResumeAfter = Pt ? "Voltar a carregar após cair" : "Resume charging after a drop of";
        public static readonly string Points = Pt ? "pontos percentuais" : "percentage points";
        public static readonly string Apply = Pt ? "Aplicar" : "Apply";

        public static readonly string SummaryLimited = Pt ? "Carrega até {0}% e volta a carregar abaixo de {1}%." : "Charges up to {0}% and resumes below {1}%.";
        public static readonly string SummaryUnlimited = Pt ? "Sem limite: carrega até 100%." : "No limit: charges to 100%.";

        public static readonly string AppliedNone = Pt ? "Nada aplicado por este app ainda (o firmware não informa o limite atual)." : "Nothing applied by this app yet (the firmware does not report the current limit).";
        public static readonly string AppliedUnlimited = Pt ? "Último aplicado por este app: sem limite." : "Last applied by this app: no limit.";
        public static readonly string AppliedLimited = Pt ? "Último aplicado por este app: {0}% (retoma abaixo de {1}%)." : "Last applied by this app: {0}% (resumes below {1}%).";

        public static readonly string Battery = Pt ? "Bateria: {0}% · {1}{2}" : "Battery: {0}% · {1}{2}";
        public static readonly string OnBattery = Pt ? "na bateria" : "on battery";
        public static readonly string Charging = Pt ? "carregando" : "charging";
        public static readonly string NotCharging = Pt ? "na tomada, sem carregar" : "plugged in, not charging";
        public static readonly string BatteryUnreadable = Pt ? "Bateria: não foi possível ler ({0})" : "Battery: could not be read ({0})";

        public static readonly string ApplyFailed = Pt ? "Não foi possível aplicar o limite: {0}" : "Could not apply the limit: {0}";
        public static readonly string NeedsAdmin = Pt ? "Execute como administrador." : "Run as administrator.";
        public static readonly string Unsupported = Pt
            ? "Driver Qualcomm PMIC GLink (QCOMBATTMGR) não encontrado. Este app só funciona em notebooks Snapdragon X."
            : "Qualcomm PMIC GLink driver (QCOMBATTMGR) not found. This app only works on Snapdragon X laptops.";
    }
}
