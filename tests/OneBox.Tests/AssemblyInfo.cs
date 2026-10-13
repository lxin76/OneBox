// 宿主路径（HostPaths）是静态配置，测试会改写它以隔离到临时目录。
// 因此禁用并行执行，避免不同测试类互相覆盖数据目录。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
