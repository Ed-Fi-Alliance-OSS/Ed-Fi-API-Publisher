using System;

namespace EdFi.Tools.ApiPublisher.Core.Processing
{
    public class ChangeWindow
    {
        private long _minChangeVersion;
        private long _maxChangeVersion;

        public long MinChangeVersion
        {
            get => _minChangeVersion;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Change versions must be greater than 0.");
                }
                
                _minChangeVersion = value;
            }
        }

        public long MaxChangeVersion
        {
            get => _maxChangeVersion;
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Change versions must be greater than 0.");
                }
                
                _maxChangeVersion = value;
            }
        }
    }
}