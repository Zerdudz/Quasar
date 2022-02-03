using System.Runtime.Serialization;

namespace Quasar.Client.IpGeoLocation
{
    [DataContract]
    public class GeoResponse
    {
        [DataMember(Name ="status")]
        public string Status { get; set; }

        [DataMember(Name = "country")]
        public string Country { get; set; }

        [DataMember(Name = "countryCode")]
        public string CountryCode { get; set; }

        [DataMember(Name = "region")]
        public string Region { get; set; }

        [DataMember(Name = "regionName")]
        public string RegionName { get; set; }

        [DataMember(Name = "city")]
        public string City { get; set; }

        [DataMember(Name = "zip")]
        public string Zip { get; set; }

        [DataMember(Name = "lat")]
        public float Lat { get; set; }

        [DataMember(Name = "lon")]
        public float Lon { get; set; }

        [DataMember(Name = "timezone")]
        public string Timezone { get; set; }

        [DataMember(Name = "isp")]
        public string ISP { get; set; }

        [DataMember(Name = "org")]
        public string Org { get; set; }

        [DataMember(Name = "as")]
        public string AS { get; set; }

        [DataMember(Name = "query")]
        public string IP { get; set; }

    }

}
