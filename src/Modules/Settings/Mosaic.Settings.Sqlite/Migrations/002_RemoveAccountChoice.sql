-- 0.18.7 移除了隐藏设置项 auth.accountChoiceMade：启动页改由导航贡献声明默认页，
-- 不再通过设置写入驱动首启路由。旧安装里残留的该行没有任何读取方，删除以免它
-- 被备份、导出或设置差异比较当成有效配置。其余设置行保持不变。
DELETE FROM settings WHERE setting_id = 'auth.accountChoiceMade';