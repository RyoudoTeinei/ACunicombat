using System;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechValidationIssue
    {
        public MechValidationSeverity severity;
        public MechPartSlot slot;
        public string code;
        public string message;

        public MechValidationIssue()
        {
        }

        public MechValidationIssue(MechValidationSeverity severity, MechPartSlot slot, string code, string message)
        {
            this.severity = severity;
            this.slot = slot;
            this.code = code;
            this.message = message;
        }
    }
}
