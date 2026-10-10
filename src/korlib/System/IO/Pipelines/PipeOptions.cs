// NeutrinoOS korlib - PipeOptions (Kestrel port milestone M2)

namespace System.IO.Pipelines
{
    public class PipeOptions
    {
        public static PipeOptions Default { get; } = new PipeOptions();

        public PipeOptions()
        {
            MinimumSegmentSize = 4096;
            PauseWriterThreshold = 65536;
            ResumeWriterThreshold = 32768;
        }

        /// <summary>Minimum size of segments rented from the pool.</summary>
        public int MinimumSegmentSize { get; set; }

        /// <summary>When unread bytes exceed this, FlushAsync does not complete until the reader catches up.</summary>
        public long PauseWriterThreshold { get; set; }

        /// <summary>Once unread bytes drop to this, a paused writer resumes.</summary>
        public long ResumeWriterThreshold { get; set; }
    }
}
