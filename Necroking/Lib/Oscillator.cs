using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Necroking.Lib;

public struct Oscillator
{
    public float freq;
    public float amplitude;
    public float phase;

    public Oscillator(float freq, float amplitude, float phase)
    {
        this.freq = freq;
        this.amplitude = amplitude;
        this.phase = phase;
    }

    public float ValueAt(float t)
    {
        float omega = freq * 2f * MathF.PI;
        return MathF.Sin(omega * t + phase) * amplitude;
    }

    public float VelocityAt(float t)
    {
        float omega = freq * 2f * MathF.PI;
        return MathF.Cos(omega * t + phase) * amplitude * omega;
    }
}
