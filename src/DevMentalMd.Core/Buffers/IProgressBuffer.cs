using System;
using System.Collections.Generic;
using System.Text;
namespace DevMentalMd.Core.Buffers;

public interface IProgressBuffer : IBuffer {
    /// <summary>Fired after each chunk is decoded (on the background thread).</summary>
    public event Action? ProgressChanged;

    /// <summary>Fired once when loading finishes (on the background thread).</summary>
    public event Action? LoadComplete;
}
