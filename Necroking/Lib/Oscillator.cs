using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Necroking.Lib;

public struct Oscillator
{
    public float Freq;
    public float Amplitude;
    public float Phase;

    public Oscillator(float freq, float amplitude, float phase)
    {
        this.Freq = freq;
        this.Amplitude = amplitude;
        this.Phase = phase;
    }

    public float ValueAt(float t)
    {
        float omega = Freq * 2f * MathF.PI;
        return MathF.Sin(omega * t + Phase) * Amplitude;
    }

    public float VelocityAt(float t)
    {
        float omega = Freq * 2f * MathF.PI;
        return MathF.Cos(omega * t + Phase) * Amplitude * omega;
    }
}
