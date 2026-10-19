# Brio

[Etheirys/Brio](https://github.com/Etheirys/Brio) 的台服移植版。
強化集體動作（GPose）攝影體驗的工具插件。

## 功能

- **角色擺姿**：可在動畫播放中調整姿勢、調整位置而不重置動作，支援疊加層
  與圖形化兩種擺姿模式。
- **新增／刪除 GPose 演員**。
- **建立自訂光源**。
- **編輯角色外觀**。
- **離線匯入／匯出 MCDF 檔案**到 GPose 演員身上。
- **切換套用在 GPose 演員上的 Penumbra 收藏集**。
- **播放 XAT 的 .xcp 攝影機檔案**。
- **新增／移除／混合 GPose 演員的動畫**，並可調整播放速度。
- **切換活動節慶**，可同時套用多個節慶效果（例如月下狂歡祭的煙火）。
- **新增／移除 GPose 演員的狀態效果**。
- **控制野外與 GPose 中的時間／天氣**。
- **重新繪製 GPose 演員**。
- **NPC 外觀套用**：可將 NPC 外觀套用到玩家角色，且不會破壞 Penumbra
  等工具的運作。

## 指令

- `/brio`：開關 Brio 主視窗（預設進入集體動作時也會自動開啟）。
- `/brio settings`：開關設定視窗；`/brio about`：開關說明視窗；`/brio help`：在聊天視窗列出子指令。

## 台服版的修改

- 特徵碼對不上台服客戶端時只停用該功能，不再讓整個插件載入失敗
- 凍結物理與 IK 加上台服警語，凍結物理寫入前先比對原始位元組，不符就不寫
- 修正多處跨幀持有遊戲物件、從背景執行緒碰角色資料的崩潰風險（套用外觀、MCDF、姿勢匯入、光源、IPC 等）
- 修正自訂光源銷毀時的記憶體損壞、拆光源拆錯燈、光源每幀更新條件寫反
- 修正生成角色的 Penumbra 集合與 Glamourer 設計套不到自己身上、Glamourer 依名字還原的條件寫反
- 停用中的按鈕與勾選框，滑鼠移上去的說明照常顯示且亮度正常
- 介面繁體中文化

## 授權

Brio 採用 [GPL 3.0 授權](https://github.com/Etheirys/Brio/blob/main/LICENSE)。

原作者：[Minmoose](https://github.com/Minmoose)（維護者與開發者）、[Asgard](https://github.com/AsgardXIV)（原始維護者與開發者）

特別感謝 Anamnesis、Dynamis、Ktisis、Penumbra、Glamourer、VFXEditor、Cammy
等專案，完整名單見
[Acknowledgements.md](https://github.com/Etheirys/Brio/blob/main/Acknowledgements.md)。
