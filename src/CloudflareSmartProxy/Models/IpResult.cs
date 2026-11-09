using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;

namespace CloudflareSmartProxy.Models
{
    public sealed class IpResult : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _selected = false;
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        private string _ip = string.Empty;
        public string Ip
        {
            get => _ip;
            set
            {
                if (_ip == value) return;
                _ip = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ip)));
            }
        }

        private int _sended;
        public int Sended
        {
            get => _sended;
            set
            {
                if (_sended == value) return;
                _sended = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sended)));
            }
        }

        private int _received;
        public int Received
        {
            get => _received;
            set
            {
                if (_received == value) return;
                _received = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Received)));
            }
        }

        private double _lossRate;
        /// <summary>丢包率 0~1（(Sended-Received)/Sended）。</summary>
        public double LossRate
        {
            get => _lossRate;
            set
            {
                if (Math.Abs(_lossRate - value) < 1e-9) return;
                _lossRate = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LossRate)));
            }
        }

        private double _avgDelayMs;
        /// <summary>平均延迟（ms）。</summary>
        public double AvgDelayMs
        {
            get => _avgDelayMs;
            set
            {
                if (Math.Abs(_avgDelayMs - value) < 1e-9) return;
                _avgDelayMs = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvgDelayMs)));
            }
        }

        private double _speedMBs;
        /// <summary>下载速度（MB/s，未测速为 0）。</summary>
        public double SpeedMBs
        {
            get => _speedMBs;
            set
            {
                if (Math.Abs(_speedMBs - value) < 1e-9) return;
                _speedMBs = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedMBs)));
            }
        }

        private string _colo = string.Empty;
        /// <summary>地区码（来自响应头，可为空）。</summary>
        public string Colo
        {
            get => _colo;
            set
            {
                if (_colo == value) return;
                _colo = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Colo)));
            }
        }

        private string _cdn = "Cloudflare";
        /// <summary>CDN 归属，用于结果表筛选与代理列表分组。默认 Cloudflare 以兼容既有数据。</summary>
        public string Cdn
        {
            get => _cdn;
            set
            {
                if (_cdn == value) return;
                _cdn = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cdn)));
            }
        }
    }
}
