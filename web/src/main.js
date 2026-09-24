import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import * as ElementPlusIconsVue from '@element-plus/icons-vue'

import App from './App.vue'
import './styles.css'
import { startRouter } from './router'
import { installAuthGuards } from './auth'

const app = createApp(App)

for (const [key, component] of Object.entries(ElementPlusIconsVue)) {
  app.component(key, component)
}

app.use(createPinia())

// 401 的全局跳转。必须在 pinia 装好之后注册：回调里要取 store，
// 而 store 的实例化依赖已激活的 pinia。
installAuthGuards()

app.use(ElementPlus, { locale: zhCn })

// 先确定当前路由再挂载，避免首帧渲染出错误页面再跳走
startRouter()

app.mount('#app')
