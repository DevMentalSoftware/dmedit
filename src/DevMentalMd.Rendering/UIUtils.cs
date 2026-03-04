using System;
using System.Collections.Generic;
using System.Text;

namespace DevMentalMd.Rendering;

public static class UIUtils {

    public static double ToPixels(this int pts) => pts * 96.0 / 72.0;
}
