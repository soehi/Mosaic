global using System.IO;

// 这些用例会启动真实进程、抢鼠标键盘并截图取色，并行执行时互相干扰（浮动窗体会被
// 前台窗口抢走点击或句柄失效），必须串行跑。
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
