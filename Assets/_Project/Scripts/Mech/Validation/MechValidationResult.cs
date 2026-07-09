using System;
using System.Collections.Generic;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechValidationResult
    {
        private readonly List<MechValidationIssue> issues = new List<MechValidationIssue>();

        public IReadOnlyList<MechValidationIssue> Issues => issues;
        public bool IsValid => ErrorCount == 0;
        public int ErrorCount { get; private set; }
        public int WarningCount { get; private set; }

        public void Add(MechValidationSeverity severity, MechPartSlot slot, string code, string message)
        {
            issues.Add(new MechValidationIssue(severity, slot, code, message));
            if (severity == MechValidationSeverity.Error)
            {
                ErrorCount++;
            }
            else if (severity == MechValidationSeverity.Warning)
            {
                WarningCount++;
            }
        }

        public void Clear()
        {
            issues.Clear();
            ErrorCount = 0;
            WarningCount = 0;
        }
    }
}
